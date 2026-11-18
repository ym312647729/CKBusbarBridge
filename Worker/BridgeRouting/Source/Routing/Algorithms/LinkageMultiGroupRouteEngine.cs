using GeometRi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.Routing;
using RouteLinkBar.Algorithm.Transport;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    internal static partial class LinkageMultiGroupRouteEngine
    {
        private const int PrioritizedOptimizationSolutionCount = 10;

        // 探索期（用户 2026-11-07 要求）：搜索开始后前 2 分钟主搜索保持全力寻找不同路径族，
        // 后台优化最多 2 路且只优化当前最短的 2 个方案；期满恢复原有分配规则。引擎内部
        // 常量，不改变 SearchOptions、JSON 或协议。
        private const long ExplorationWindowMilliseconds = 120000L;
        private const int ExplorationOptimizedSolutionCount = 2;

        public static IReadOnlyList<LinkageRouteSolution> Solve(
            LinkageMultiGroupRouteRequest requestedRouteRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            if (requestedRouteRequest?.Groups?.Any(LinkageRouteTerminalHoleLapValidator.HasStartHoles) != true)
                return SolveWithSectionSpecifications(requestedRouteRequest, algorithmKind, progress, cancellationToken);

            // 两个阶段共享原总时限；首发目标时间内先搜索居中孔组，未找到再开放偏心。
            // 这是有限搜索偏好，不把“未找到”宣称为连续空间无解。
            var preferenceStopwatch = Stopwatch.StartNew();
            using (var totalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var centeredCancellation = CancellationTokenSource.CreateLinkedTokenSource(totalCancellation.Token))
            {
                var centeredPublishedSolutions = new ConcurrentDictionary<string,LinkageRouteSolution>(StringComparer.Ordinal);
                if (requestedRouteRequest.SearchOptions.EnableAutomaticTotalSearchLimit &&
                    !requestedRouteRequest.SearchOptions.PrioritizeAllSolutions)
                    totalCancellation.CancelAfter(requestedRouteRequest.SearchOptions.TotalSearchLimitMilliseconds);
                centeredCancellation.CancelAfter(Math.Max(1, requestedRouteRequest.SearchOptions.FirstSolutionTargetMilliseconds));
                var centeredProgress = new InlineProgress<LinkageRouteProgress>(update => {
                    if (update?.PublishedSolution?.IsSuccess == true)
                    {
                        centeredPublishedSolutions.TryAdd(LinkageRouteTopologySignature.CreateStableGeometrySignature(update.PublishedSolution),
                            LinkageRouteWorkerContractMapper.FromWorkerSolution(
                                LinkageRouteWorkerContractMapper.ToWorkerSolution(update.PublishedSolution)));
                        centeredCancellation.CancelAfter(Timeout.Infinite); // 已首发则保留原总时限内的居中优化。
                    }
                    if (update != null) update.ElapsedMilliseconds = preferenceStopwatch.ElapsedMilliseconds;
                    progress?.Report(update);
                });
                IReadOnlyList<LinkageRouteSolution> centeredSolutions;
                try
                {
                    centeredSolutions = SolveWithSectionSpecifications(
                        requestedRouteRequest.WithCenteredTerminalHoleGroups(), algorithmKind,
                        centeredProgress, centeredCancellation.Token);
                }
                catch (OperationCanceledException centeredStageCancellation) when (
                    centeredCancellation.IsCancellationRequested &&
                    !totalCancellation.IsCancellationRequested &&
                    centeredStageCancellation.CancellationToken == centeredCancellation.Token)
                {
                    // 只消费本阶段自己的取消；用户取消和其它令牌异常继续向上传播。
                    centeredSolutions = Array.Empty<LinkageRouteSolution>();
                }
                if (centeredSolutions.Any(solution => solution?.IsSuccess == true) || totalCancellation.IsCancellationRequested)
                    return centeredSolutions;
                if (!centeredPublishedSolutions.IsEmpty)
                    return centeredPublishedSolutions.Values.OrderBy(solution=>solution.ActualCopperLength).ToList();
                progress?.Report(new LinkageRouteProgress { AlgorithmKind = algorithmKind,
                    ElapsedMilliseconds = preferenceStopwatch.ElapsedMilliseconds,
                    Message = "孔组居中优先搜索未找到完整方案，继续在原总时限内尝试满足孔边距的偏心搭接。" });
                var fallbackProgress = new InlineProgress<LinkageRouteProgress>(update => {
                    if (update != null) update.ElapsedMilliseconds = preferenceStopwatch.ElapsedMilliseconds;
                    progress?.Report(update);
                });
                return SolveWithSectionSpecifications(requestedRouteRequest, algorithmKind, fallbackProgress, totalCancellation.Token);
            }
        }

        private static IReadOnlyList<LinkageRouteSolution> SolveWithSectionSpecifications(
            LinkageMultiGroupRouteRequest requestedRouteRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            requestedRouteRequest = LinkageRouteCabinetBridgeBuilder.Prepare(requestedRouteRequest);
            var hasSectionSpecificSettings = requestedRouteRequest?.Groups != null &&
                requestedRouteRequest.Groups.Any(routeGroupRequest =>
                    routeGroupRequest?.HasSectionSpecificBusbarSettings(
                        requestedRouteRequest) == true);
            if (!hasSectionSpecificSettings)
            {
                return SolveCore(
                    requestedRouteRequest,
                    algorithmKind,
                    progress,
                    cancellationToken);
            }

            // 异规格请求保留一条通用骨架枚举分支，但分支仍携带原始
            // 分段规格；通用视图只影响骨架生成，最终候选必须重新完成
            // 真实排宽、排厚、搭接和实体校验后才可发布。
            using (var sectionSearchCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
            {
                var genericSpecificationRequest =
                    CreateGenericSpecificationSearchRequest(requestedRouteRequest);
                var envelopeSpecificationRequest =
                    CreateWidthEnvelopeSearchRequest(requestedRouteRequest);
                if (genericSpecificationRequest != null)
                    genericSpecificationRequest.IsSectionSpecificationAuxiliarySearch = true;
                if (envelopeSpecificationRequest != null)
                    envelopeSpecificationRequest.IsSectionSpecificationAuxiliarySearch = true;
                var materializedFallbackSolutions = new ConcurrentDictionary<
                    string,
                    LinkageRouteSolution>(StringComparer.Ordinal);
                var materializationFailureDiagnostics =
                    new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                var primaryProgress = new InlineProgress<LinkageRouteProgress>(
                    rawProgress =>
                    {
                        if (rawProgress?.PublishedSolution == null)
                        {
                            progress?.Report(rawProgress);
                            return;
                        }

                        if (!TryValidatePhysicalSolutionMaterialization(
                                requestedRouteRequest,
                                rawProgress.PublishedSolution))
                        {
                            return;
                        }

                        progress?.Report(rawProgress);
                    });
                Func<LinkageMultiGroupRouteRequest, bool,
                    InlineProgress<LinkageRouteProgress>> createFallbackProgress =
                    (searchRequest, isEnvelope) => new InlineProgress<LinkageRouteProgress>(
                    rawProgress =>
                    {
                        if (rawProgress == null)
                        {
                            return;
                        }

                        if (rawProgress.PublishedSolution == null)
                        {
                            progress?.Report(new LinkageRouteProgress
                            {
                                AlgorithmKind = algorithmKind,
                                ElapsedMilliseconds = rawProgress.ElapsedMilliseconds,
                                ProcessedStateCount = rawProgress.ProcessedStateCount,
                                Message = (isEnvelope
                                    ? "异规格实际排宽包络搜索："
                                    : "异规格通用骨架搜索：") +
                                    (rawProgress.Message ?? string.Empty)
                            });
                            return;
                        }

                        var physicalFallbackSolution =
                            MaterializeGenericSolutionWithPhysicalSpecificationsCancelable(
                                requestedRouteRequest,
                                searchRequest,
                                rawProgress.PublishedSolution,
                                out var materializationFailureReason, sectionSearchCancellationSource.Token);
                        if (physicalFallbackSolution == null ||
                            physicalFallbackSolution.IsSuccess != true ||
                            !IsFinalRequestedClearanceSatisfied(
                                physicalFallbackSolution.MinimumClearance,
                                requestedRouteRequest.InsulationClearance))
                        {
                            if (!string.IsNullOrWhiteSpace(
                                    materializationFailureReason) &&
                                materializationFailureDiagnostics.TryAdd(
                                    materializationFailureReason,
                                    0))
                            {
                                progress?.Report(new LinkageRouteProgress
                                {
                                    AlgorithmKind = algorithmKind,
                                    ElapsedMilliseconds =
                                        rawProgress.ElapsedMilliseconds,
                                    ProcessedStateCount =
                                        rawProgress.ProcessedStateCount,
                                    Message = (isEnvelope
                                        ? "异规格排宽包络实体化未通过："
                                        : "异规格实体化未通过：") +
                                        materializationFailureReason
                                });
                            }
                            return;
                        }

                        var physicalSolutionSignature =
                            LinkageRouteTopologySignature
                                .CreateStableGeometrySignature(
                                    physicalFallbackSolution);
                        if (!materializedFallbackSolutions.TryAdd(
                            physicalSolutionSignature,
                            physicalFallbackSolution))
                        {
                            return;
                        }

                        progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = algorithmKind,
                            ElapsedMilliseconds = rawProgress.ElapsedMilliseconds,
                            ProcessedStateCount = rawProgress.ProcessedStateCount,
                            PublishedSolution = physicalFallbackSolution,
                            Message = isEnvelope
                                ? "异规格排宽包络骨架已按真实分段规格实体化并通过输出复核。"
                                : "异规格通用骨架已按真实分段规格重新实体化并通过输出复核。"
                        });
                    });
                var fallbackProgress = createFallbackProgress(
                    genericSpecificationRequest, false);
                var envelopeProgress = envelopeSpecificationRequest == null
                    ? null
                    : createFallbackProgress(envelopeSpecificationRequest, true);
                var physicalFiniteSeedsReady = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var finiteSeedCancellationRegistration = sectionSearchCancellationSource.Token.Register(
                    () => physicalFiniteSeedsReady.TrySetResult(true));
                var primaryTask = Task.Run(
                    () =>
                    {
                        try
                        {
                            return SolveCore(requestedRouteRequest, requestedRouteRequest,
                                algorithmKind, primaryProgress, sectionSearchCancellationSource.Token,
                                () => physicalFiniteSeedsReady.TrySetResult(true));
                        }
                        finally
                        {
                            // Input failure, cancellation or an unexpected
                            // primary failure must not strand auxiliary tasks.
                            physicalFiniteSeedsReady.TrySetResult(true);
                        }
                    },
                    sectionSearchCancellationSource.Token);
                var fallbackTask = Task.Run(
                    async () =>
                    {
                        await physicalFiniteSeedsReady.Task.ConfigureAwait(false);
                        sectionSearchCancellationSource.Token.ThrowIfCancellationRequested();
                        return SolveCore(genericSpecificationRequest, algorithmKind,
                            fallbackProgress, sectionSearchCancellationSource.Token);
                    },
                    sectionSearchCancellationSource.Token);
                Task<IReadOnlyList<LinkageRouteSolution>> envelopeTask = null;
                if (envelopeSpecificationRequest != null)
                {
                    envelopeTask = Task.Run(
                        async () =>
                        {
                            await physicalFiniteSeedsReady.Task.ConfigureAwait(false);
                            sectionSearchCancellationSource.Token.ThrowIfCancellationRequested();
                            return SolveCore(envelopeSpecificationRequest, algorithmKind,
                                envelopeProgress, sectionSearchCancellationSource.Token);
                        },
                        sectionSearchCancellationSource.Token);
                }
                // Actual finite seeds get an independent first turn. Once that
                // bounded stage ends, every original full auxiliary search and
                // background optimizer remains available; publication itself
                // does not cancel the other branches.
                var searchTasks = new List<Task> { primaryTask, fallbackTask };
                if (envelopeTask != null)
                    searchTasks.Add(envelopeTask);
                WaitForSectionSearchTasks(searchTasks, sectionSearchCancellationSource);
                var primarySolutions = primaryTask.Status == TaskStatus.RanToCompletion
                    ? primaryTask.Result
                    : Array.Empty<LinkageRouteSolution>();
                var fallbackSolutions = fallbackTask.Status == TaskStatus.RanToCompletion
                    ? fallbackTask.Result
                    : Array.Empty<LinkageRouteSolution>();
                IReadOnlyList<LinkageRouteSolution> envelopeSolutions =
                    envelopeTask?.Status == TaskStatus.RanToCompletion
                        ? envelopeTask.Result
                        : Array.Empty<LinkageRouteSolution>();
                var primaryQualifiedSolutions = new List<LinkageRouteSolution>();
                foreach (var primarySolution in primarySolutions.Where(
                    routeSolution => routeSolution?.IsSuccess == true))
                {
                    if (TryValidatePhysicalSolutionOutput(
                            requestedRouteRequest,
                            primarySolution) &&
                        TryValidatePhysicalSolutionMaterialization(
                            requestedRouteRequest,
                            primarySolution))
                    {
                        primaryQualifiedSolutions.Add(primarySolution);
                    }
                }
                if (primaryQualifiedSolutions.Count > 0)
                {
                    return primaryQualifiedSolutions;
                }

                var fallbackBatches = new[]
                {
                    new { SearchRequest = genericSpecificationRequest,
                        Solutions = fallbackSolutions },
                    new { SearchRequest = envelopeSpecificationRequest,
                        Solutions = envelopeSolutions }
                };
                foreach (var fallbackBatch in fallbackBatches.Where(batch =>
                    batch.SearchRequest != null))
                foreach (var fallbackSolution in fallbackBatch.Solutions
                    .Where(routeSolution => routeSolution?.IsSuccess == true &&
                        IsFinalRequestedClearanceSatisfied(
                            routeSolution.MinimumClearance,
                            fallbackBatch.SearchRequest.InsulationClearance)))
                {
                    var physicalFallbackSolution =
                        MaterializeGenericSolutionWithPhysicalSpecificationsCancelable(
                            requestedRouteRequest,
                            fallbackBatch.SearchRequest,
                            fallbackSolution, out _, sectionSearchCancellationSource.Token);
                    if (physicalFallbackSolution == null ||
                        physicalFallbackSolution.IsSuccess != true ||
                        !IsFinalRequestedClearanceSatisfied(
                            physicalFallbackSolution.MinimumClearance,
                            requestedRouteRequest.InsulationClearance))
                    {
                        continue;
                    }

                    var physicalSolutionSignature =
                        LinkageRouteTopologySignature
                            .CreateStableGeometrySignature(
                                physicalFallbackSolution);
                    if (materializedFallbackSolutions.TryAdd(
                        physicalSolutionSignature,
                        physicalFallbackSolution))
                    {
                        progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = algorithmKind,
                            PublishedSolution = physicalFallbackSolution,
                            Message = "异规格通用骨架已按真实分段规格重新实体化并通过输出复核。"
                        });
                    }
                }

                return materializedFallbackSolutions.Values.ToList();
            }
        }

        private static LinkageRouteSolution
            MaterializeGenericSolutionWithPhysicalSpecifications(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageMultiGroupRouteRequest genericSearchRequest,
            LinkageRouteSolution genericSolution)
        {
            return MaterializeGenericSolutionWithPhysicalSpecifications(
                outputRouteRequest,
                genericSearchRequest,
                genericSolution,
                out _);
        }

        private static LinkageRouteSolution
            MaterializeGenericSolutionWithPhysicalSpecifications(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageMultiGroupRouteRequest genericSearchRequest,
            LinkageRouteSolution genericSolution,
            out string failureReason)
        {
            return MaterializeGenericSolutionWithPhysicalSpecificationsCancelable(outputRouteRequest,
                genericSearchRequest, genericSolution, out failureReason, CancellationToken.None);
        }

        private static LinkageRouteSolution MaterializeGenericSolutionWithPhysicalSpecificationsCancelable(
            LinkageMultiGroupRouteRequest outputRouteRequest, LinkageMultiGroupRouteRequest genericSearchRequest,
            LinkageRouteSolution genericSolution, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = null;
            if (outputRouteRequest == null || genericSearchRequest == null ||
                genericSolution?.GroupSolutions == null)
            {
                failureReason = "缺少输出请求、通用搜索请求或通用方案。";
                return null;
            }

            var physicalSolution = new LinkageRouteSolution
            {
                SchemeIndex = genericSolution.SchemeIndex,
                OptimizationSourceTopologySignature =
                    genericSolution.OptimizationSourceTopologySignature,
                AlgorithmKind = genericSolution.AlgorithmKind,
                IsSuccess = genericSolution.IsSuccess,
                IsCancelled = genericSolution.IsCancelled,
                ActualCopperLength = genericSolution.ActualCopperLength,
                PerpendicularLapCount = genericSolution.PerpendicularLapCount,
                MinimumClearance = genericSolution.MinimumClearance,
                RequestedInsulationClearance =
                    genericSolution.RequestedInsulationClearance,
                RequestedLapInsulationClearance =
                    genericSolution.RequestedLapInsulationClearance,
                SearchInsulationClearance = genericSolution.SearchInsulationClearance,
                IsRelaxedClearanceSolution = genericSolution.IsRelaxedClearanceSolution
            };
            var physicalConversions =
                new List<LinkageRouteCenterlineConversionResult>();
            foreach (var genericGroupSolution in genericSolution.GroupSolutions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputGroupRequest = outputRouteRequest.Groups.FirstOrDefault(
                    routeGroupRequest => string.Equals(
                        routeGroupRequest?.GroupKey,
                        genericGroupSolution?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                var genericGroupRequest = genericSearchRequest.Groups.FirstOrDefault(
                    routeGroupRequest => string.Equals(
                        routeGroupRequest?.GroupKey,
                        genericGroupSolution?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (outputGroupRequest == null || genericGroupRequest == null ||
                    genericGroupSolution?.ManufacturableCenterline == null ||
                    genericGroupSolution.ManufacturableCenterline.Count < 2)
                {
                    failureReason = "逻辑组缺少请求映射或有效中心线：" +
                        (genericGroupSolution?.GroupKey ?? "<未命名组>");
                    return null;
                }

                var physicalConversion = CreatePhysicalConversionForGenericGroupCancelable(
                    outputRouteRequest,
                    genericSearchRequest,
                    outputGroupRequest,
                    genericGroupRequest,
                    genericGroupSolution, cancellationToken);
                if (physicalConversion == null ||
                    physicalConversion.CenterlineParts.Count == 0 ||
                    physicalConversion.StackMembers.Count == 0)
                {
                    failureReason = "逻辑组未生成真实规格实体：" +
                        (genericGroupSolution.GroupKey ?? "<未命名组>");
                    return null;
                }
                if (!physicalConversion.IsValid)
                {
                    failureReason = "逻辑组真实规格转换失败：" +
                        (genericGroupSolution.GroupKey ?? "<未命名组>") +
                        "；" + string.Join("；",
                            physicalConversion.Diagnostics ??
                            new List<string>());
                    return null;
                }

                // 普通候选已经完成搜索、绝缘筛选和排序；此处才应用双始端
                // 连接排与实际始端物理段的厚向输出修正，避免把该平移带入
                // 候选门禁或改变首方案生成。
                LinkageRouteTerminalAccessoryBuilder
                    .ApplyOutputTerminalBridgeLayerAlignment(
                        physicalConversion);
                if (!LinkageRouteTerminalAccessoryBuilder.TryValidateConnectionOutput(
                        physicalConversion,
                        outputRouteRequest,
                        outputGroupRequest,
                        out var connectionOutputFailureReason))
                {
                    physicalSolution.Diagnostics.Add(
                        new LinkageRouteFailureDiagnostic
                        {
                            GroupKey = genericGroupSolution.GroupKey,
                            Stage = LinkageRouteFailureStage.Conversion,
                            Message = connectionOutputFailureReason
                        });
                    failureReason = connectionOutputFailureReason;
                    return null;
                }
                physicalConversions.Add(physicalConversion);

                var physicalGroupSolution = new LinkageGroupRouteSolution
                {
                    GroupKey = genericGroupSolution.GroupKey,
                    AxisSkeleton = genericGroupSolution.AxisSkeleton,
                    ManufacturableCenterline = genericGroupSolution
                        .ManufacturableCenterline,
                    PlanarParts = physicalConversion.CenterlineParts,
                    StackMembers = physicalConversion.StackMembers,
                    Accessories = physicalConversion.Accessories,
                    ActualCopperLength = physicalConversion.ActualCopperLength,
                    PerpendicularLapCount = genericGroupSolution.PerpendicularLapCount,
                    IsConvertedShape = genericGroupSolution.IsConvertedShape,
                    TopologySignature = genericGroupSolution.TopologySignature
                };
                foreach (var bridgeMemberIndex in genericGroupSolution
                    .ExpectedDoubleStartBridgeMemberIndices)
                {
                    physicalGroupSolution.ExpectedDoubleStartBridgeMemberIndices
                        .Add(bridgeMemberIndex);
                }
                foreach (var physicalSectionIndex in physicalConversion
                    .DoubleStartPhysicalBridgeSectionIndices)
                {
                    physicalGroupSolution.DoubleStartPhysicalBridgeSectionIndices
                        .Add(physicalSectionIndex);
                }
                physicalSolution.GroupSolutions.Add(physicalGroupSolution);
                foreach (var outputGeometryDiagnostic in physicalConversion
                    .Diagnostics
                    .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
                    .Distinct(StringComparer.Ordinal))
                {
                    physicalSolution.Diagnostics.Add(
                        new LinkageRouteFailureDiagnostic
                        {
                            GroupKey = genericGroupSolution.GroupKey,
                            Stage = LinkageRouteFailureStage.Conversion,
                            Message = outputGeometryDiagnostic
                        });
                }
            }

            if (!TryApplyUShapeOutputPolicy(outputRouteRequest, physicalSolution,
                    out var physicalUShapeChanged, out failureReason))
                return null;
            ApplyPhysicalSolutionOutputRegressionAdjustments(
                physicalSolution,
                outputRouteRequest,
                physicalUShapeChanged ? null : physicalConversions);

            if (!TryValidateFinalPhysicalSolution(
                    outputRouteRequest,
                    physicalSolution,
                    out failureReason))
            {
                return null;
            }

            if (!TryGetPhysicalSolutionMinimumClearance(
                    outputRouteRequest,
                    physicalSolution,
                    out var exactMinimumClearance) ||
                exactMinimumClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                    outputRouteRequest.InsulationClearance)
            {
                failureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "真实规格方案绝缘距离 {0:F3} mm 小于请求值 {1:F3} mm。",
                    exactMinimumClearance,
                    outputRouteRequest.InsulationClearance);
                return null;
            }

            physicalSolution.MinimumClearance = exactMinimumClearance;
            physicalSolution.ActualCopperLength = physicalSolution.GroupSolutions
                .Sum(groupSolution => groupSolution.ActualCopperLength);
            physicalSolution.RequestedInsulationClearance =
                outputRouteRequest.InsulationClearance;
            physicalSolution.RequestedLapInsulationClearance =
                outputRouteRequest.LapInsulationClearance;
            physicalSolution.SearchInsulationClearance =
                genericSearchRequest.InsulationClearance;
            physicalSolution.IsRelaxedClearanceSolution = false;

            return physicalSolution;
        }

        // 普通候选的物理排可能在骨架快照之后已完成落位。同一组的实际
        // 排规完全不变时保留那套实体；有任一规格变化仍按真实排规重转换。
        private static LinkageRouteCenterlineConversionResult
            CreatePhysicalConversionForGenericGroup(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageMultiGroupRouteRequest genericSearchRequest,
            LinkageRouteGroupRequest outputGroupRequest,
            LinkageRouteGroupRequest genericGroupRequest,
            LinkageGroupRouteSolution genericGroupSolution)
        {
            return CreatePhysicalConversionForGenericGroupCancelable(outputRouteRequest, genericSearchRequest,
                outputGroupRequest, genericGroupRequest, genericGroupSolution, CancellationToken.None);
        }

        private static LinkageRouteCenterlineConversionResult CreatePhysicalConversionForGenericGroupCancelable(
            LinkageMultiGroupRouteRequest outputRouteRequest, LinkageMultiGroupRouteRequest genericSearchRequest,
            LinkageRouteGroupRequest outputGroupRequest, LinkageRouteGroupRequest genericGroupRequest,
            LinkageGroupRouteSolution genericGroupSolution, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outputGroupRequest == null || genericGroupRequest == null ||
                genericGroupSolution == null)
            {
                return null;
            }

            var physicalSpecificationsUnchanged =
                Math.Abs(outputGroupRequest.GetBusbarWidth(outputRouteRequest) -
                    genericGroupRequest.GetBusbarWidth(genericSearchRequest)) <= 1e-8d &&
                Math.Abs(outputGroupRequest.GetBusbarThickness(outputRouteRequest) -
                    genericGroupRequest.GetBusbarThickness(genericSearchRequest)) <= 1e-8d &&
                outputGroupRequest.GetStackCount(outputRouteRequest) ==
                    genericGroupRequest.GetStackCount(genericSearchRequest) &&
                Math.Abs(outputGroupRequest.GetStartLapBusbarWidth(outputRouteRequest) -
                    genericGroupRequest.GetStartLapBusbarWidth(genericSearchRequest)) <= 1e-8d &&
                Math.Abs(outputGroupRequest.GetStartLapBusbarThickness(outputRouteRequest) -
                    genericGroupRequest.GetStartLapBusbarThickness(genericSearchRequest)) <= 1e-8d &&
                outputGroupRequest.GetStartLapStackCount(outputRouteRequest) ==
                    genericGroupRequest.GetStartLapStackCount(genericSearchRequest) &&
                Math.Abs(outputGroupRequest.GetCabinetTopLapBusbarWidth(outputRouteRequest) -
                    genericGroupRequest.GetCabinetTopLapBusbarWidth(genericSearchRequest)) <= 1e-8d &&
                Math.Abs(outputGroupRequest.GetCabinetTopLapBusbarThickness(outputRouteRequest) -
                    genericGroupRequest.GetCabinetTopLapBusbarThickness(genericSearchRequest)) <= 1e-8d &&
                outputGroupRequest.GetCabinetTopLapStackCount(outputRouteRequest) ==
                    genericGroupRequest.GetCabinetTopLapStackCount(genericSearchRequest);
            if (!physicalSpecificationsUnchanged ||
                genericGroupSolution.PlanarParts?.Count == 0 ||
                genericGroupSolution.StackMembers?.Count == 0)
            {
                return LinkageRouteCenterlineConverter.ConvertForUContactSideCancelable(
                    genericGroupSolution.ManufacturableCenterline,
                    outputRouteRequest,
                    outputGroupRequest, false, genericGroupSolution.USplitContactSide, cancellationToken);
            }

            var preservedConversion = CreateOutputAdjustmentConversion(
                genericGroupSolution);
            preservedConversion.CenterlineParts.Clear();
            foreach (var planarPart in genericGroupSolution.PlanarParts)
                preservedConversion.CenterlineParts.Add(planarPart.Copy());
            preservedConversion.StackMembers.Clear();
            foreach (var stackMember in genericGroupSolution.StackMembers)
            {
                preservedConversion.StackMembers.Add(new LinkageStackedRouteMember
                {
                    MemberIndex = stackMember.MemberIndex,
                    CenterlineOffset = stackMember.CenterlineOffset,
                    PlanarParts = stackMember.PlanarParts
                        .Select(planarPart => planarPart.Copy()).ToList()
                });
            }
            preservedConversion.Accessories.Clear();
            foreach (var routeAccessory in genericGroupSolution.Accessories)
            {
                preservedConversion.Accessories.Add(new LinkageRouteAccessory
                {
                    Kind = routeAccessory.Kind,
                    TerminalSide = routeAccessory.TerminalSide,
                    RelatedGeneratedMemberIndex = routeAccessory.RelatedGeneratedMemberIndex,
                    Setting = routeAccessory.Setting?.Copy(),
                    SupportBlockKey = routeAccessory.SupportBlockKey
                });
            }
            preservedConversion.ActualCopperLength =
                genericGroupSolution.ActualCopperLength;
            preservedConversion.IsValid = true;
            return preservedConversion;
        }

        private static LinkageMultiGroupRouteRequest
            CreateGenericSpecificationSearchRequest(
                LinkageMultiGroupRouteRequest sourceRouteRequest)
        {
            var genericSearchRequest = LinkageRouteWorkerContractMapper
                .FromWorkerRequest(
                    LinkageRouteWorkerContractMapper.ToWorkerRequest(
                        sourceRouteRequest));
            genericSearchRequest.Groups = (sourceRouteRequest.Groups ??
                    new List<LinkageRouteGroupRequest>())
                .Select(routeGroupRequest => routeGroupRequest == null
                    ? null
                    : routeGroupRequest
                        .CreateGenericPathSearchSpecificationView(
                            sourceRouteRequest))
                .ToList();
            genericSearchRequest.RuntimeControl = sourceRouteRequest.RuntimeControl;
            genericSearchRequest.RequireCenteredTerminalHoleGroups = sourceRouteRequest.RequireCenteredTerminalHoleGroups;
            genericSearchRequest.UseLegacyDoubleStartHorizontalTopologyProposal =
                sourceRouteRequest.UseLegacyDoubleStartHorizontalTopologyProposal;
            var sectionLayerRealignmentReserve = (sourceRouteRequest.Groups ??
                    new List<LinkageRouteGroupRequest>())
                .Where(routeGroupRequest => routeGroupRequest != null &&
                    routeGroupRequest.HasSectionSpecificBusbarSettings(
                        sourceRouteRequest) &&
                    routeGroupRequest.GetBusbarThickness(sourceRouteRequest) >=
                        10d - 1e-6d)
                .Select(routeGroupRequest => Math.Max(
                    Math.Abs(
                        routeGroupRequest.GetStartLapBusbarThickness(
                            sourceRouteRequest) -
                        routeGroupRequest.GetBusbarThickness(
                            sourceRouteRequest)),
                    Math.Abs(
                        routeGroupRequest.GetCabinetTopLapBusbarThickness(
                            sourceRouteRequest) -
                        routeGroupRequest.GetBusbarThickness(
                            sourceRouteRequest))))
                .DefaultIfEmpty(0d)
                .Max();
            // 仅对 30 mm 及以上的通用距离预留异规格层位迁移量。
            // 低于 30 mm 的输入不能因为排厚差被提前抬高阈值；这些候选
            // 仍由最终真实规格实体化和完整净距复核决定是否发布。否则
            // 19 mm 等历史方案会在通用候选池阶段被全部筛掉，尚未进入
            // 可修正的几何调整流程就会超时。
            genericSearchRequest.InsulationClearance =
                sourceRouteRequest.InsulationClearance +
                (sourceRouteRequest.InsulationClearance >= 30d - 1e-6d
                    ? sectionLayerRealignmentReserve
                    : 0d);
            genericSearchRequest.SearchOptions.MaximumSolutionsPerAlgorithm =
                Math.Max(
                    sourceRouteRequest.SearchOptions.MaximumSolutionsPerAlgorithm,
                    64);
            genericSearchRequest.SearchOptions.MaximumCandidatesPerGroup =
                Math.Max(
                    sourceRouteRequest.SearchOptions.MaximumCandidatesPerGroup,
                    192);
            return genericSearchRequest;
        }

        // 真实端段比中段宽时，另补一条最大真实排宽的普通骨架候选。
        // 原中段宽分支不变，所有输出仍按原请求的真实分段实体化和终检。
        private static LinkageMultiGroupRouteRequest
            CreateWidthEnvelopeSearchRequest(
            LinkageMultiGroupRouteRequest sourceRouteRequest)
        {
            var envelopeSearchRequest = CreateGenericSpecificationSearchRequest(
                sourceRouteRequest);
            var requiresAdditionalSearch = false;
            for (var groupIndex = 0;
                groupIndex < envelopeSearchRequest.Groups.Count; groupIndex++)
            {
                var sourceGroup = sourceRouteRequest.Groups[groupIndex];
                var envelopeGroup = envelopeSearchRequest.Groups[groupIndex];
                if (sourceGroup == null || envelopeGroup == null)
                    continue;
                var middleWidth = sourceGroup.GetBusbarWidth(sourceRouteRequest);
                var maximumActualWidth = Math.Max(middleWidth, Math.Max(
                    sourceGroup.GetStartLapBusbarWidth(sourceRouteRequest),
                    sourceGroup.GetCabinetTopLapBusbarWidth(sourceRouteRequest)));
                if (maximumActualWidth <= middleWidth + 1e-8d)
                    continue;
                envelopeGroup.BusbarWidth = maximumActualWidth;
                requiresAdditionalSearch = true;
            }
            return requiresAdditionalSearch ? envelopeSearchRequest : null;
        }

        private static bool TryGetPhysicalSolutionMinimumClearance(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out double minimumClearance)
        {
            minimumClearance = double.PositiveInfinity;
            if (routeRequest == null || routeSolution?.GroupSolutions == null)
            {
                return false;
            }

            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedAndObstacleSolids,
                    out _))
            {
                return false;
            }

            var generatedSolidsByGroup = new Dictionary<
                string,
                IReadOnlyList<LinkageRouteSolid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var groupSolution in routeSolution.GroupSolutions)
            {
                if (groupSolution == null)
                {
                    continue;
                }

                if (groupSolution.MinimumClearanceFaceGroups == null)
                {
                    groupSolution.MinimumClearanceFaceGroups =
                        new List<LinkageRouteClearanceFaceGroup>();
                }
                else
                {
                    groupSolution.MinimumClearanceFaceGroups.Clear();
                }
            }

            foreach (var groupSolution in routeSolution.GroupSolutions)
            {
                var groupRequest = routeRequest.Groups.FirstOrDefault(
                    currentGroupRequest => string.Equals(
                        currentGroupRequest?.GroupKey,
                        groupSolution?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                var generatedSolids = BuildPhysicalSolutionSolids(
                    groupSolution,
                    groupRequest);
                if (groupRequest == null || generatedSolids.Count == 0)
                {
                    return false;
                }

                generatedSolidsByGroup[groupSolution.GroupKey] = generatedSolids;
            }

            var minimumClearancePairs = new List<PhysicalMinimumClearancePair>();

            foreach (var generatedGroup in generatedSolidsByGroup)
            {
                foreach (var fixedSolid in fixedAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                {
                    if (!ParticipatesInInsulationClearance(fixedSolid) ||
                        IsSameClearanceGroup(
                            generatedGroup.Value[0],
                            fixedSolid))
                    {
                        continue;
                    }

                    foreach (var generatedSolid in generatedGroup.Value)
                    {
                        if (IsSameClearanceGroup(
                                generatedSolid,
                                fixedSolid))
                        {
                            continue;
                        }

                        RegisterPhysicalMinimumClearancePair(
                            generatedGroup.Key,
                            generatedSolid,
                            fixedSolid,
                            LinkageRouteSolidDistance.GetMinimumDistance(
                                generatedSolid,
                                fixedSolid),
                            ref minimumClearance,
                            minimumClearancePairs);
                    }
                }
            }

            var orderedGroups = generatedSolidsByGroup.ToList();
            for (var firstGroupIndex = 0;
                firstGroupIndex < orderedGroups.Count;
                firstGroupIndex++)
            {
                for (var secondGroupIndex = firstGroupIndex + 1;
                    secondGroupIndex < orderedGroups.Count;
                    secondGroupIndex++)
                {
                    var firstGroup = orderedGroups[firstGroupIndex];
                    var secondGroup = orderedGroups[secondGroupIndex];
                    var firstGroupSolids = firstGroup.Value;
                    var secondGroupSolids = secondGroup.Value;
                    foreach (var firstSolid in firstGroupSolids)
                    {
                        foreach (var secondSolid in secondGroupSolids)
                        {
                            if (IsSameClearanceGroup(firstSolid, secondSolid))
                            {
                                continue;
                            }

                            RegisterPhysicalMinimumClearancePair(
                                firstGroup.Key,
                                firstSolid,
                                secondSolid,
                                LinkageRouteSolidDistance.GetMinimumDistance(
                                    firstSolid,
                                    secondSolid),
                                ref minimumClearance,
                                minimumClearancePairs);
                        }
                    }
                }
            }

            if (double.IsNaN(minimumClearance) ||
                double.IsNegativeInfinity(minimumClearance))
            {
                return false;
            }

            foreach (var minimumClearancePair in minimumClearancePairs)
            {
                var generatedGroupSolution = routeSolution.GroupSolutions
                    .FirstOrDefault(groupSolution => string.Equals(
                        groupSolution?.GroupKey,
                        minimumClearancePair.GeneratedGroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (generatedGroupSolution == null)
                {
                    continue;
                }

                generatedGroupSolution.MinimumClearanceFaceGroups.Add(
                    LinkageRouteCandidateFactory.CreateClearanceFaceGroup(
                        minimumClearancePair.GeneratedSolid,
                        minimumClearancePair.OtherSolid,
                        minimumClearancePair.Distance));
            }

            return true;
        }

        private static void RegisterPhysicalMinimumClearancePair(
            string generatedGroupKey,
            LinkageRouteSolid generatedSolid,
            LinkageRouteSolid otherSolid,
            double distance,
            ref double minimumClearance,
            ICollection<PhysicalMinimumClearancePair> minimumClearancePairs)
        {
            const double minimumClearancePairTolerance = 1e-6d;
            if (double.IsNaN(distance) || double.IsInfinity(distance))
            {
                return;
            }

            if (distance + minimumClearancePairTolerance < minimumClearance)
            {
                minimumClearance = distance;
                minimumClearancePairs.Clear();
            }

            if (Math.Abs(distance - minimumClearance) >
                minimumClearancePairTolerance)
            {
                return;
            }

            if (minimumClearancePairs.Any(existingPair =>
                    string.Equals(
                        existingPair.GeneratedGroupKey,
                        generatedGroupKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    existingPair.GeneratedSolid.SegmentIndex ==
                        generatedSolid.SegmentIndex &&
                    string.Equals(
                        existingPair.OtherSolid.GroupKey,
                        otherSolid.GroupKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    existingPair.OtherSolid.SegmentIndex ==
                        otherSolid.SegmentIndex &&
                    existingPair.OtherSolid.SolidRole == otherSolid.SolidRole &&
                    string.Equals(
                        existingPair.OtherSolid.SolidSourceKey,
                        otherSolid.SolidSourceKey,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            minimumClearancePairs.Add(new PhysicalMinimumClearancePair
            {
                GeneratedGroupKey = generatedGroupKey,
                GeneratedSolid = generatedSolid,
                OtherSolid = otherSolid,
                Distance = distance
            });
        }

        private static bool ParticipatesInInsulationClearance(
            LinkageRouteSolid routeSolid)
        {
            return routeSolid != null &&
                !routeSolid.IsForbiddenTerminalRegion &&
                routeSolid.SolidRole != LinkageRouteSolidRole.InputObstacle &&
                routeSolid.SolidRole !=
                    LinkageRouteSolidRole.ForbiddenTerminalRegion;
        }

        private sealed class PhysicalMinimumClearancePair
        {
            public string GeneratedGroupKey { get; set; }
            public LinkageRouteSolid GeneratedSolid { get; set; }
            public LinkageRouteSolid OtherSolid { get; set; }
            public double Distance { get; set; }
        }

        internal static bool TryValidateSchemeLibraryOutput(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason,
            bool preserveHistoricalSpecifications = false)
        {
            return TryValidateSchemeLibraryOutputWithStackEvidence(routeRequest, routeSolution,
                out failureReason, preserveHistoricalSpecifications, null);
        }

        internal static bool TryValidateSchemeLibraryOutputWithStackEvidence(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason,
            bool preserveHistoricalSpecifications,
            LinkageHistoricalStackPlacementEvidence historicalStackEvidence)
        {
            failureReason = null;
            if (routeRequest == null || routeSolution == null ||
                routeSolution.GroupSolutions == null ||
                routeSolution.GroupSolutions.Count == 0)
            {
                failureReason = "方案库重算结果为空。";
                return false;
            }

            // 历史逐排方案已经以柜顶真实厚面为锁定锚点完成落位；
            // 普通搜索的叠排层位回归会再次平移原排，破坏该真实搭接。
            if (preserveHistoricalSpecifications &&
                !LinkageSchemeLibraryRouteRebuilder.TryValidateHistoricalFixedClearance(
                    routeRequest, out failureReason)) return false;
            if (!preserveHistoricalSpecifications)
            {
                ApplyPhysicalSolutionOutputRegressionAdjustments(
                    routeSolution,
                    routeRequest);
            }
            else
            {
                foreach (var historicalGroup in routeSolution.GroupSolutions)
                {
                    var historicalConversion = CreateOutputAdjustmentConversion(historicalGroup);
                    LinkageRouteSupportBlockLayers.SplitOversized(historicalConversion);
                    historicalGroup.Accessories = historicalConversion.Accessories.ToList();
                }
            }
            if (!TryValidateFinalPhysicalSolutionWithStackEvidence(
                    routeRequest,
                    routeSolution,
                    out failureReason,
                    preserveHistoricalSpecifications, historicalStackEvidence))
            {
                return false;
            }

            if (preserveHistoricalSpecifications &&
                !TryValidateSchemeLibraryActualLapAndEnvelopes(
                    routeRequest, routeSolution, out failureReason))
            {
                return false;
            }

            if (!TryGetPhysicalSolutionMinimumClearance(
                    routeRequest,
                    routeSolution,
                    out var exactMinimumClearance) ||
                exactMinimumClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                    routeRequest.InsulationClearance)
            {
                failureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "方案库重算结果实际绝缘净距 {0:F3} mm 小于要求 {1:F3} mm。",
                    exactMinimumClearance,
                    routeRequest.InsulationClearance);
                return false;
            }

            routeSolution.MinimumClearance = exactMinimumClearance;
            LinkageRouteCopperLength.Recalculate(routeRequest, routeSolution);
            routeSolution.RequestedInsulationClearance =
                routeRequest.InsulationClearance;
            routeSolution.RequestedLapInsulationClearance =
                routeRequest.LapInsulationClearance;
            routeSolution.SearchInsulationClearance =
                routeRequest.InsulationClearance;
            routeSolution.IsRelaxedClearanceSolution = false;
            return true;
        }

        private static bool TryValidateSchemeLibraryActualLapAndEnvelopes(
            LinkageMultiGroupRouteRequest request,
            LinkageRouteSolution solution,
            out string failureReason)
        {
            failureReason = null;
            if (!LinkageSchemeLibraryRouteRebuilder.TryValidateHistoricalPerpendicularEndpoints(
                request, solution, out failureReason))
            {
                return false;
            }
            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(
                    request, out var fixedAndObstacleSolids,
                    out failureReason))
            {
                return false;
            }
            var originalCopper = fixedAndObstacleSolids
                .Where(solid => solid?.SolidRole ==
                    LinkageRouteSolidRole.FixedTerminalBusbar).ToList();
            var generatedCopper = new List<LinkageRouteSolid>();
            var lapEnvelopes = new List<LinkageRouteSolid>();
            foreach (var group in solution.GroupSolutions)
            {
                var groupRequest = request.Groups.FirstOrDefault(item =>
                    string.Equals(item?.GroupKey, group?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (groupRequest == null || group?.StackMembers == null)
                {
                    failureReason = "方案库实体终检缺少组或逐层铜排。";
                    return false;
                }
                if (!LinkageSchemeLibraryRouteRebuilder.TryValidateHistoricalContactGraph(
                    groupRequest, group, out failureReason)) return false;

                var candidate = new LinkageRouteCandidate
                {
                    GroupRequest = groupRequest,
                    PlanarConversion = new LinkageRouteCenterlineConversionResult()
                };
                foreach (var member in group.StackMembers)
                {
                    candidate.PlanarConversion.StackMembers.Add(member);
                }
                foreach (var accessory in group.Accessories ??
                    new List<LinkageRouteAccessory>())
                {
                    candidate.PlanarConversion.Accessories.Add(accessory);
                }
                var groupEnvelopes = LinkageRouteCandidateFactory
                    .BuildHistoricalLapClearanceEnvelopeSolids(candidate, request,
                        groupRequest, LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                if (request.LapInsulationClearance > 1e-6d &&
                    groupEnvelopes.Count < group.PlanarParts.Count + 1)
                {
                    failureReason = "方案库组 " + group.GroupKey +
                        " 的实际搭接面未完整生成单侧绝缘实体。";
                    return false;
                }
                lapEnvelopes.AddRange(groupEnvelopes);
                var groupCopper = BuildPhysicalSolutionSolids(group,
                    groupRequest);
                generatedCopper.AddRange(groupCopper);
                originalCopper.AddRange(groupCopper);
            }

            foreach (var generated in generatedCopper)
            foreach (var other in originalCopper)
            {
                if (ReferenceEquals(generated, other) ||
                    IsSameClearanceGroup(generated, other) ||
                    !LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                        generated, other, LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    continue;
                }
                failureReason = "方案库异组原始铜排实体发生正体积重叠：" +
                    generated.GroupKey + " → " + other.GroupKey + "。";
                return false;
            }

            foreach (var envelope in lapEnvelopes)
            foreach (var copper in originalCopper)
            {
                if (IsSameClearanceGroup(envelope, copper) ||
                    !LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                        envelope, copper, LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    continue;
                }
                failureReason = "方案库搭接面单侧绝缘实体与其他绝缘组的" +
                    "原始铜排发生正体积相交：" + envelope.GroupKey +
                    " → " + copper.GroupKey + "。";
                return false;
            }
            return true;
        }

        private static bool HasSchemeLibraryTerminalContact(
            PlanarBendBusbarSetting part,
            LinkageTerminalAssembly terminal,
            LinkageGroupRouteSolution group,
            LinkageStackedRouteMember member,
            LinkageRouteTerminalSide terminalSide)
        {
            var fixedSettings = terminal?.Members?.Select(item =>
                item?.FixedBusbar) ?? Enumerable.Empty<PlanarBendBusbarSetting>();
            if (fixedSettings.Any(fixedSetting =>
                HasSchemeLibraryActualContact(part, fixedSetting)))
            {
                return true;
            }
            var terminalAccessories = (group.Accessories ?? new List<LinkageRouteAccessory>())
                .Where(accessory => accessory?.Setting != null &&
                    accessory.TerminalSide == terminalSide &&
                    accessory.RelatedGeneratedMemberIndex ==
                        member.MemberIndex).Select(accessory => accessory.Setting).ToList();
            // 超厚垫块拆成多层后，不能要求单块同时接触固定端和生成排。
            // 只沿本成员、本端部附件的实际满搭接面验证连续接触链。
            var contactedSettings = new HashSet<PlanarBendBusbarSetting> { part };
            var pendingContacts = new Queue<PlanarBendBusbarSetting>();
            pendingContacts.Enqueue(part);
            while (pendingContacts.Count > 0)
            {
                var currentContact = pendingContacts.Dequeue();
                if (fixedSettings.Any(fixedSetting => HasSchemeLibraryActualContact(currentContact, fixedSetting)))
                    return true;
                foreach (var accessorySetting in terminalAccessories)
                    if (!contactedSettings.Contains(accessorySetting) &&
                        HasSchemeLibraryActualContact(currentContact, accessorySetting))
                    {
                        contactedSettings.Add(accessorySetting);
                        pendingContacts.Enqueue(accessorySetting);
                    }
            }
            return false;
        }

        private static bool HasSchemeLibraryActualContact(
            PlanarBendBusbarSetting first,
            PlanarBendBusbarSetting second)
        {
            if (first?.PointList?.Count < 2 ||
                second?.PointList?.Count < 2)
            {
                return false;
            }
            foreach (var firstStart in new[] { true, false })
            foreach (var secondStart in new[] { true, false })
            {
                if (LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(first, firstStart,
                        second, secondStart,
                        Math.Max(first.BusBarWidth,
                            second.BusBarWidth), out _,
                        tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryValidatePhysicalSolutionOutput(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution)
        {
            ApplyPhysicalSolutionOutputRegressionAdjustments(
                routeSolution,
                routeRequest);
            if (!TryValidatePhysicalSolutionMaterialization(
                    routeRequest,
                    routeSolution))
            {
                return false;
            }
            if (!TryValidateFinalPhysicalSolution(
                    routeRequest,
                    routeSolution,
                    out _))
            {
                return false;
            }
            if (!TryGetPhysicalSolutionMinimumClearance(
                    routeRequest,
                    routeSolution,
                    out var exactMinimumClearance) ||
                exactMinimumClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison < routeRequest.InsulationClearance)
            {
                return false;
            }

            routeSolution.MinimumClearance = exactMinimumClearance;
            routeSolution.RequestedInsulationClearance =
                routeRequest.InsulationClearance;
            routeSolution.RequestedLapInsulationClearance =
                routeRequest.LapInsulationClearance;
            routeSolution.IsRelaxedClearanceSolution = false;
            return true;
        }

        private static bool TryValidateFinalPhysicalSolution(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason,
            bool preserveHistoricalSpecifications = false)
        {
            if (!TryValidateFinalPhysicalSolutionWithStackEvidence(routeRequest, routeSolution,
                out failureReason, preserveHistoricalSpecifications, null))
            {
                return false;
            }
            // 方案库有独立的历史实体搭接图终检；普通算法必须基于最终移动后的
            // 实际铜排重新生成临时单侧绝缘盒，不能沿用候选阶段的旧位置。
            return preserveHistoricalSpecifications || TryValidateFinalGenericLapClearance(
                routeRequest, routeSolution, out failureReason);
        }

        private static bool TryValidateFinalGenericLapClearance(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason)
        {
            failureReason = null;
            if (routeRequest.LapInsulationClearance <= 1e-6d)
            {
                return true;
            }
            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(
                routeRequest, out var finalFixedSolids, out failureReason))
            {
                return false;
            }
            var finalOriginalCopper = finalFixedSolids.Where(solid =>
                solid.SolidRole == LinkageRouteSolidRole.FixedTerminalBusbar).ToList();
            var finalLapEnvelopes = new List<LinkageRouteSolid>();
            foreach (var finalGroup in routeSolution.GroupSolutions)
            {
                var finalGroupRequest = routeRequest.Groups.FirstOrDefault(group =>
                    string.Equals(group.GroupKey, finalGroup.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (finalGroupRequest == null)
                {
                    failureReason = "最终搭接面绝缘复核缺少组请求：" + finalGroup.GroupKey;
                    return false;
                }
                finalOriginalCopper.AddRange(BuildPhysicalSolutionSolids(finalGroup, finalGroupRequest));
                var finalGroupEnvelopes = LinkageRouteCandidateFactory.BuildLapClearanceEnvelopeSolids(
                    new LinkageRouteCandidate
                    {
                        GroupRequest = finalGroupRequest,
                        PlanarConversion = CreateOutputAdjustmentConversion(finalGroup)
                    }, routeRequest, finalGroupRequest,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                if (finalGroupEnvelopes.Count == 0)
                {
                    failureReason = "最终组 " + finalGroup.GroupKey + " 未生成实际搭接面的单侧绝缘实体。";
                    return false;
                }
                finalLapEnvelopes.AddRange(finalGroupEnvelopes);
            }
            // 两个集合严格分开：绝缘盒不是铜排，不能互相校验或再次膨胀。
            foreach (var finalEnvelope in finalLapEnvelopes)
            foreach (var finalCopper in finalOriginalCopper)
            {
                if (IsSameClearanceGroup(finalEnvelope, finalCopper) ||
                    !LinkageRouteSolidDistance.HasPositiveVolumeIntersection(finalEnvelope,
                        finalCopper, LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    continue;
                }
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "最终搭接面绝缘违规（要求 {0:F6} mm）：组 {1} 的接口 {2} 与组 {3} 原始铜排段 {4} 正体积相交；此项不是通用净距不足。",
                    routeRequest.LapInsulationClearance, finalEnvelope.GroupKey,
                    finalEnvelope.SolidSourceKey, finalCopper.GroupKey, finalCopper.SegmentIndex);
                return false;
            }
            return true;
        }

        private static bool TryValidateFinalPhysicalSolutionWithStackEvidence(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason,
            bool preserveHistoricalSpecifications,
            LinkageHistoricalStackPlacementEvidence historicalStackEvidence,
            bool validateTerminalHoles = true)
        {
            failureReason = null;
            if (routeRequest == null || routeSolution == null)
            {
                failureReason = "最终输出实体复核缺少请求或方案。";
                return false;
            }

            if (validateTerminalHoles)
            {
                foreach (var holeGroup in routeSolution.GroupSolutions)
                {
                    var holeRequest = routeRequest.Groups.FirstOrDefault(item => string.Equals(item.GroupKey,
                        holeGroup.GroupKey, StringComparison.OrdinalIgnoreCase));
                    if (holeRequest == null) continue;
                    var holeConversion = CreateOutputAdjustmentConversion(holeGroup);
                    if (!LinkageRouteTerminalHoleLapValidator.HasStartHoles(holeRequest) &&
                        LinkageRouteTerminalHoleLapValidator.TryValidate(routeRequest,holeRequest,holeConversion,out _)) continue;
                    var holeRepaired = LinkageRouteTerminalHoleLapValidator.TryRepair(routeRequest, holeRequest, holeConversion,
                        () => {
                            holeGroup.Accessories = holeConversion.Accessories.ToList();
                            return TryValidateFinalPhysicalSolutionWithStackEvidence(routeRequest, routeSolution,
                                       out _, preserveHistoricalSpecifications, historicalStackEvidence, validateTerminalHoles: false) &&
                                   (preserveHistoricalSpecifications || TryValidateFinalGenericLapClearance(routeRequest, routeSolution, out _));
                        }, out failureReason);
                    holeGroup.Accessories = holeConversion.Accessories.ToList();
                    if (!holeRepaired) return false;
                    holeGroup.ActualCopperLength = holeConversion.ActualCopperLength;
                    LinkageRouteCopperLength.Recalculate(routeRequest, routeSolution);
                }
            }
            if (routeSolution.GroupSolutions.Any(group => group.InternalSupportRebuildFailed))
            {
                failureReason = "最终内部搭接间隙补块未完成，不能以回滚旧垫块代替填满实际间隙。";
                return false;
            }

            if (!TryValidatePhysicalSolutionMaterializationWithStackEvidence(
                    routeRequest,
                    routeSolution,
                    preserveHistoricalSpecifications, historicalStackEvidence))
            {
                failureReason = "最终输出叠排数量、规格或连接排结构不符合输入参数。";
                return false;
            }

            if (!TryValidatePhysicalSolutionStructure(
                    routeSolution,
                    out var intersectionDiagnostic,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
            {
                failureReason = intersectionDiagnostic ??
                    "最终输出实体在回归调整后仍存在正体积干涉。";
                return false;
            }

            if (!TryValidatePhysicalTerminalLapRegions(
                    routeSolution,
                    out failureReason))
            {
                return false;
            }

            if (!TryValidatePhysicalSolutionStaticRestrictions(
                    routeRequest,
                    routeSolution,
                    out failureReason))
            {
                return false;
            }

            foreach (var groupSolution in routeSolution.GroupSolutions)
            {
                var routeGroupRequest = routeRequest.Groups?.FirstOrDefault(
                    groupRequest => string.Equals(
                        groupRequest?.GroupKey,
                        groupSolution?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                var outputConversion = CreateOutputAdjustmentConversion(
                    groupSolution);
                if (routeGroupRequest == null || outputConversion == null)
                {
                    failureReason = "最终输出实体缺少对应逻辑组。";
                    return false;
                }

                if (!LinkageRouteCabinetBridgeBuilder.TryValidate(outputConversion, routeGroupRequest, out failureReason))
                    return false;

                if (!preserveHistoricalSpecifications && LinkageRouteTerminalHoleLapValidator.HasStartHoles(routeGroupRequest) &&
                    !LinkageRouteCandidateFactory.ValidateSingleMemberFinalTerminalLapCoverage(
                        outputConversion, routeRequest, routeGroupRequest, out failureReason)) return false;

                if (validateTerminalHoles && !LinkageRouteTerminalHoleLapValidator.TryValidate(
                        routeRequest, routeGroupRequest, outputConversion, out failureReason)) return false;

                if (!LinkageRouteFixedTerminalEndLapValidator.TryValidateConversion(
                        routeRequest, routeGroupRequest, outputConversion, out failureReason))
                    return false;

                // 端部和内部搭接段可能在后续联动回归中再次移动；实体化阶段
                // 已验证的内部折弯余量不能代替最终输出位置的复核。
                if (!preserveHistoricalSpecifications &&
                    !LinkageRouteSectionSpecificationPostProcessor.TryValidateRoundedLapInterfaces(
                        outputConversion, routeRequest, routeGroupRequest, out failureReason))
                    return false;
                if (!preserveHistoricalSpecifications &&
                    !LinkageRouteSectionSpecificationPostProcessor
                        .TryValidateFinalTerminalPerpendicularLapBendReserves(
                            routeRequest,
                            routeGroupRequest,
                            groupSolution,
                            out failureReason))
                {
                    return false;
                }

                if (!preserveHistoricalSpecifications &&
                    !LinkageRouteSectionSpecificationPostProcessor
                        .TryValidateFinalInternalPerpendicularLapBendReserves(
                            routeRequest,
                            routeGroupRequest,
                            groupSolution,
                            out failureReason))
                {
                    return false;
                }

                if (preserveHistoricalSpecifications
                    ? !LinkageSchemeLibraryRouteRebuilder.TryValidateHistoricalContactGraph(
                        routeGroupRequest, groupSolution, out failureReason)
                    : !LinkageRouteTerminalAccessoryBuilder
                        .TryValidateConnectionOutput(
                            outputConversion,
                            routeRequest,
                            routeGroupRequest,
                            out failureReason))
                {
                    return false;
                }

                if (!LinkageRouteTerminalAccessoryBuilder
                        .TryValidateOutputTerminalBridgeLayerAlignment(
                            outputConversion,
                            out failureReason,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    return false;
                }

                if (!LinkageRouteTerminalAccessoryBuilder
                        .TryValidateSingleStartHorizontalLapLayersForOutput(
                            outputConversion,
                            routeGroupRequest.StartTerminal,
                            routeRequest.RequireSingleStartMiddleLap,
                            out failureReason,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    return false;
                }

                // Historical members retain their own point order. Their
                // internal interfaces are checked by the actual-face gate
                // below (all endpoint combinations, no unresolved gaps), not
                // the ordinary preceding.End -> following.Start convention.
                if (!preserveHistoricalSpecifications &&
                    !LinkageRouteSectionSpecificationPostProcessor
                        .TryValidateInternalPhysicalLapClosure(
                            outputConversion,
                            true,
                            out _,
                            out failureReason,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidatePhysicalTerminalLapRegions(
            LinkageRouteSolution routeSolution,
            out string failureReason)
        {
            failureReason = null;
            foreach (var groupSolution in routeSolution?.GroupSolutions ??
                new List<LinkageGroupRouteSolution>())
            {
                foreach (var stackMember in groupSolution?.StackMembers ??
                    new List<LinkageStackedRouteMember>())
                {
                    for (var partIndex = 0;
                        partIndex < (stackMember?.PlanarParts?.Count ?? 0);
                        partIndex++)
                    {
                        if (LinkageRouteTerminalLapRegionValidator
                            .HasNoPositiveAreaOverlap(
                                stackMember.PlanarParts[partIndex],
                                out var lapRegionFailureReason,
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                        {
                            continue;
                        }

                        failureReason = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "组 {0} 第 {1} 层第 {2} 段首尾搭接区域重叠：{3}",
                            groupSolution.GroupKey,
                            stackMember.MemberIndex + 1,
                            partIndex + 1,
                            lapRegionFailureReason);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryValidatePhysicalSolutionStaticRestrictions(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            out string failureReason)
        {
            failureReason = null;
            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedAndObstacleSolids,
                    out failureReason))
            {
                return false;
            }

            var restrictionSolids = fixedAndObstacleSolids
                .Where(staticSolid => staticSolid != null &&
                    (staticSolid.IsForbiddenTerminalRegion ||
                     staticSolid.SolidRole ==
                        LinkageRouteSolidRole.InputObstacle ||
                     staticSolid.SolidRole ==
                        LinkageRouteSolidRole.ForbiddenTerminalRegion))
                .ToList();

            foreach (var groupSolution in routeSolution.GroupSolutions ??
                new List<LinkageGroupRouteSolution>())
            {
                var sameGroupFixedTerminalSolids = fixedAndObstacleSolids
                    .Where(staticSolid => staticSolid != null &&
                        staticSolid.SolidRole ==
                            LinkageRouteSolidRole.FixedTerminalBusbar &&
                        string.Equals(
                            staticSolid.GroupKey,
                            groupSolution?.GroupKey,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var generatedSolid in BuildPhysicalSolutionSolids(
                    groupSolution,
                    routeRequest.Groups?.FirstOrDefault(groupRequest =>
                        string.Equals(
                            groupRequest?.GroupKey,
                            groupSolution?.GroupKey,
                            StringComparison.OrdinalIgnoreCase))))
                {
                    if (routeRequest.UseExplicitContainmentBox &&
                        !LinkageRouteCandidateFactory.IsInsideContainment(
                            new[] { generatedSolid }, routeRequest,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                    {
                        failureReason = "最终输出铜排超出允许包围盒，超出0.5mm尺寸验收容差。";
                        return false;
                    }
                    var intersectedRestriction = restrictionSolids
                        .FirstOrDefault(restrictionSolid =>
                            LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                                    generatedSolid,
                                    restrictionSolid,
                                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison));
                    if (intersectedRestriction != null)
                    {
                        failureReason = intersectedRestriction.SolidRole ==
                            LinkageRouteSolidRole.InputObstacle
                                ? "最终输出铜排与输入障碍物发生正体积相交：" +
                                  (intersectedRestriction.SolidSourceKey ??
                                   "<未命名障碍物>") + "。"
                                : "最终输出铜排进入始端或柜顶不可伸出侧整体禁区，超出0.5mm尺寸验收容差。";
                        return false;
                    }

                    var intersectedFixedTerminal = sameGroupFixedTerminalSolids
                        .FirstOrDefault(fixedTerminalSolid =>
                            LinkageRouteSolidDistance
                                .HasPositiveVolumeIntersection(
                                    generatedSolid,
                                    fixedTerminalSolid,
                                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison));
                    if (intersectedFixedTerminal != null)
                    {
                        failureReason =
                            "最终输出铜排与本组始端或柜顶固定排发生正体积重叠：" +
                            (intersectedFixedTerminal.SolidSourceKey ??
                             "<未命名固定排>") + "。";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryValidatePhysicalSolutionMaterialization(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            bool preserveHistoricalSpecifications = false)
        {
            return TryValidatePhysicalSolutionMaterializationWithStackEvidence(routeRequest,
                routeSolution, preserveHistoricalSpecifications, null);
        }

        private static bool TryValidatePhysicalSolutionMaterializationWithStackEvidence(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteSolution routeSolution,
            bool preserveHistoricalSpecifications,
            LinkageHistoricalStackPlacementEvidence historicalStackEvidence)
        {
            if (routeRequest?.Groups == null ||
                routeSolution?.GroupSolutions == null)
            {
                return false;
            }

            foreach (var groupSolution in routeSolution.GroupSolutions)
            {
                if (groupSolution == null)
                {
                    return false;
                }

                var routeGroupRequest = routeRequest.Groups.FirstOrDefault(
                    groupRequest => string.Equals(
                        groupRequest?.GroupKey,
                        groupSolution?.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (routeGroupRequest == null)
                {
                    return false;
                }

                var physicalSectionCount = groupSolution.PlanarParts?.Count ?? 0;
                var orderedMembers = (groupSolution.StackMembers ??
                        new List<LinkageStackedRouteMember>())
                    .OrderBy(stackMember => stackMember?.MemberIndex ?? int.MaxValue)
                    .ToList();
                if (physicalSectionCount <= 0 || orderedMembers.Count == 0 ||
                    orderedMembers.Any(stackMember => stackMember == null ||
                        stackMember.PlanarParts == null ||
                        stackMember.PlanarParts.Count == 0) ||
                    !orderedMembers.Select((stackMember, memberPosition) =>
                        stackMember.MemberIndex == memberPosition).All(isContiguous =>
                        isContiguous))
                {
                    return false;
                }

                var physicalSettings = (groupSolution.StackMembers ??
                        new List<LinkageStackedRouteMember>())
                    .Where(stackMember => stackMember?.PlanarParts != null)
                    .SelectMany(stackMember => stackMember.PlanarParts)
                    .Where(planarSetting => planarSetting != null)
                    .ToList();
                var logicalSpecificationSectionCount = groupSolution.PlanarParts
                    .All(planarSetting =>
                        planarSetting != null &&
                        (planarSetting.SpecificationSectionIndex >= 0 ||
                         planarSetting.PhysicalSectionIndex >= 0))
                        ? groupSolution.PlanarParts.Max(planarSetting =>
                              planarSetting.SpecificationSectionIndex >= 0
                                  ? planarSetting.SpecificationSectionIndex
                                  : planarSetting.PhysicalSectionIndex) + 1
                        : physicalSectionCount;
                if (preserveHistoricalSpecifications)
                {
                    // 匹配方案按数据库逐根原排落位，首/中/末规格来自历史
                    // 铜排类，不把当前通用搜索的三段规格强行覆盖到原排。
                    // 仍严格核对每层物理排数量、索引、尺寸有效性及后续
                    // 搭接/实体/净距门禁。
                    if ((historicalStackEvidence == null
                            ? orderedMembers.Count != routeGroupRequest.GetStackCount(routeRequest)
                            : !historicalStackEvidence.HasExpectedSections(routeRequest, routeGroupRequest, groupSolution)) ||
                        physicalSettings.Any(planarSetting =>
                            planarSetting.BusBarWidth <= 0d ||
                            planarSetting.BusBarThickness <= 0d))
                    {
                        return false;
                    }
                }
                else if (routeGroupRequest.HasSectionSpecificBusbarSettings(
                        routeRequest))
                {
                    var expectedMaximumStackCount = Enumerable.Range(
                            0,
                            logicalSpecificationSectionCount)
                        .Select(sectionIndex => routeGroupRequest.GetSectionStackCount(
                            routeRequest,
                            sectionIndex,
                            logicalSpecificationSectionCount))
                        .DefaultIfEmpty(0)
                        .Max();
                    if (orderedMembers.Count != expectedMaximumStackCount ||
                        !Enumerable.Range(
                            0,
                            logicalSpecificationSectionCount).All(
                        physicalSectionIndex => HasExpectedPhysicalSection(
                            routeRequest,
                            routeGroupRequest,
                            logicalSpecificationSectionCount,
                            physicalSectionIndex,
                            physicalSettings)))
                    {
                        return false;
                    }
                }
                else
                {
                    var expectedStackCount = routeGroupRequest.GetStackCount(
                        routeRequest);
                    if (orderedMembers.Count != expectedStackCount ||
                        orderedMembers.Any(stackMember =>
                            stackMember.PlanarParts.Count != physicalSectionCount) ||
                        physicalSettings.Count != expectedStackCount *
                            physicalSectionCount ||
                        physicalSettings.Any(planarSetting =>
                            planarSetting == null ||
                            planarSetting.BusBarWidth <= 0d ||
                            planarSetting.BusBarThickness <= 0d ||
                            Math.Abs(planarSetting.BusBarWidth -
                                routeGroupRequest.GetBusbarWidth(routeRequest)) >
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                            Math.Abs(planarSetting.BusBarThickness -
                                routeGroupRequest.GetBusbarThickness(routeRequest)) >
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                    {
                        return false;
                    }
                }

                if (!routeRequest.AllowUShapeRoutes &&
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(
                        physicalSettings))
                {
                    return false;
                }

                var outputConversion = CreateOutputAdjustmentConversion(groupSolution);
                // 历史方案的连接排可能已是原中间实体或分支，不能再要求普通生成器的桥排附件。
                // 匹配逐源签名保留规格与全部原接口，此处仍验证全部固定端和生成实体真实连通。
                if (preserveHistoricalSpecifications
                    ? !LinkageSchemeLibraryRouteRebuilder.TryValidateHistoricalContactGraph(
                        routeGroupRequest, groupSolution, out _)
                    : !LinkageRouteTerminalAccessoryBuilder.TryValidateConnectionOutput(
                        outputConversion,
                        routeRequest,
                        routeGroupRequest,
                        out _))
                {
                    return false;
                }
            }

            return routeSolution.GroupSolutions.Count ==
                routeRequest.Groups.Count;
        }

        private static bool HasExpectedPhysicalSection(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int physicalSectionCount,
            int physicalSectionIndex,
            IReadOnlyCollection<PlanarBendBusbarSetting> physicalSettings)
        {
            var isCabinetTopSection = physicalSectionCount <= 1 ||
                physicalSectionIndex >= physicalSectionCount - 1;
            var isStartSection = !isCabinetTopSection &&
                physicalSectionIndex == 0;
            var expectedWidth = isCabinetTopSection
                ? routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)
                : isStartSection
                    ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                    : routeGroupRequest.GetBusbarWidth(routeRequest);
            var expectedThickness = isCabinetTopSection
                ? routeGroupRequest.GetCabinetTopLapBusbarThickness(routeRequest)
                : isStartSection
                    ? routeGroupRequest.GetStartLapBusbarThickness(routeRequest)
                    : routeGroupRequest.GetBusbarThickness(routeRequest);
            var expectedStackCount = isCabinetTopSection
                ? routeGroupRequest.GetCabinetTopLapStackCount(routeRequest)
                : isStartSection
                    ? routeGroupRequest.GetStartLapStackCount(routeRequest)
                    : routeGroupRequest.GetStackCount(routeRequest);
            var sectionSettings = physicalSettings.Where(planarSetting =>
                (planarSetting.SpecificationSectionIndex >= 0
                    ? planarSetting.SpecificationSectionIndex
                    : planarSetting.PhysicalSectionIndex) ==
                physicalSectionIndex)
                .ToList();
            return sectionSettings.Count >= expectedStackCount &&
                sectionSettings.Count % expectedStackCount == 0 &&
                sectionSettings.All(planarSetting =>
                    Math.Abs(planarSetting.BusBarWidth - expectedWidth) <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison &&
                    Math.Abs(planarSetting.BusBarThickness - expectedThickness) <=
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
        }

        private static List<LinkageRouteSolid> BuildPhysicalSolutionSolids(
            LinkageGroupRouteSolution groupSolution,
            LinkageRouteGroupRequest groupRequest)
        {
            var generatedSolids = new List<LinkageRouteSolid>();
            if (groupSolution == null || groupRequest == null)
            {
                return generatedSolids;
            }

            var planarSettings = (groupSolution.StackMembers ??
                    new List<LinkageStackedRouteMember>())
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                // 普通算法附件保持原口径。匹配专用 historical-interface
                // 是实际铜垫块，必须参加净距、固定端/障碍物碰撞及搭接包络校验。
                .Concat((groupSolution.Accessories ??
                        new List<LinkageRouteAccessory>())
                    .Where(routeAccessory =>
                        (routeAccessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge ||
                         routeAccessory?.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge ||
                         (routeAccessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                          (routeAccessory.TerminalSide == LinkageRouteTerminalSide.End ? groupRequest.EndTerminal : groupRequest.StartTerminal)
                              ?.CabinetBridgeAssembly != null) ||
                         routeAccessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                         (routeAccessory.SupportBlockKey ?? string.Empty).StartsWith("historical-interface:", StringComparison.Ordinal)) &&
                        routeAccessory.Setting != null)
                    .Select(routeAccessory => routeAccessory.Setting));
            var solidIndex = 0;
            foreach (var planarSetting in planarSettings)
            {
                if (planarSetting?.PointList == null ||
                    planarSetting.PointList.Count < 2 ||
                    planarSetting.WidthVector == null ||
                    planarSetting.BusBarWidth <= 0d ||
                    planarSetting.BusBarThickness <= 0d)
                {
                    continue;
                }

                for (var pointIndex = 0;
                    pointIndex < planarSetting.PointList.Count - 1;
                    pointIndex++)
                {
                    var segmentStart = planarSetting.PointList[pointIndex];
                    var segmentEnd = planarSetting.PointList[pointIndex + 1];
                    var segmentDirection = new Vector3d(
                        segmentStart,
                        segmentEnd);
                    if (segmentStart == null || segmentEnd == null ||
                        segmentDirection.Norm <= 1e-8d)
                    {
                        continue;
                    }

                    var widthDirection = planarSetting.WidthVector.Normalized;
                    var thicknessDirection = segmentDirection
                        .Cross(widthDirection)
                        .Normalized;
                    if (widthDirection == null || thicknessDirection == null)
                    {
                        continue;
                    }

                    generatedSolids.Add(LinkageRouteSolidGeometry.CreateSegmentSolid(
                        groupSolution.GroupKey,
                        segmentStart,
                        segmentEnd,
                        widthDirection,
                        thicknessDirection,
                        planarSetting.BusBarWidth,
                        planarSetting.BusBarThickness,
                        solidIndex++,
                        groupRequest.GetClearanceGroupKey(),
                        LinkageRouteSolidRole.GeneratedBusbar));
                }
            }

            return generatedSolids;
        }

        internal static bool TryValidatePhysicalSolutionStructure(
            LinkageRouteSolution routeSolution,
            out string intersectionDiagnostic,
            double acceptanceTolerance = LinkageRouteGeometryTolerances.PositiveVolume)
        {
            intersectionDiagnostic = null;
            foreach (var groupSolution in routeSolution?.GroupSolutions ??
                new List<LinkageGroupRouteSolution>())
            {
                var orderedStackMembers = (groupSolution.StackMembers ??
                        new List<LinkageStackedRouteMember>())
                    .Where(stackMember => stackMember?.PlanarParts != null &&
                        stackMember.PlanarParts.Count > 0)
                    .OrderBy(stackMember => stackMember.MemberIndex)
                    .ToList();
                var memberSolids = orderedStackMembers.ToDictionary(
                    stackMember => stackMember.MemberIndex,
                    stackMember => BuildPhysicalPlanarSettingSolids(
                        groupSolution.GroupKey,
                        stackMember.PlanarParts,
                        groupSolution.GroupKey));
                foreach (var stackMember in orderedStackMembers)
                {
                    var physicalPartSolids = stackMember.PlanarParts
                        .Select(planarPart => BuildPhysicalPlanarSettingSolids(
                            groupSolution.GroupKey,
                            new[] { planarPart },
                            groupSolution.GroupKey))
                        .ToList();
                    for (var firstPhysicalPartIndex = 0;
                        firstPhysicalPartIndex < physicalPartSolids.Count;
                        firstPhysicalPartIndex++)
                    {
                        for (var secondPhysicalPartIndex =
                                firstPhysicalPartIndex + 1;
                            secondPhysicalPartIndex < physicalPartSolids.Count;
                            secondPhysicalPartIndex++)
                        {
                            LinkageRouteSolid intersectingFirstSolid = null;
                            LinkageRouteSolid intersectingSecondSolid = null;
                            foreach (var firstSolid in physicalPartSolids[
                                firstPhysicalPartIndex])
                            {
                                intersectingSecondSolid = physicalPartSolids[
                                        secondPhysicalPartIndex]
                                    .FirstOrDefault(secondSolid =>
                                        LinkageRouteSolidDistance
                                            .HasPositiveVolumeIntersection(
                                                firstSolid,
                                                secondSolid,
                                                acceptanceTolerance));
                                if (intersectingSecondSolid != null)
                                {
                                    intersectingFirstSolid = firstSolid;
                                    break;
                                }
                            }

                            if (intersectingFirstSolid == null ||
                                intersectingSecondSolid == null)
                            {
                                continue;
                            }

                            intersectionDiagnostic = string.Format(
                                CultureInfo.InvariantCulture,
                                "组 {0} 的最终输出成员 {1} 内部物理排 {2} 与 {3} " +
                                "在层位回归调整后仍存在正体积重叠；排 {2}={4}；排 {3}={5}；相交线段={6}↔{7}。",
                                groupSolution.GroupKey ?? "<未命名组>",
                                stackMember.MemberIndex + 1,
                                firstPhysicalPartIndex + 1,
                                secondPhysicalPartIndex + 1,
                                DescribePlanarSetting(
                                    stackMember.PlanarParts[
                                        firstPhysicalPartIndex]),
                                DescribePlanarSetting(
                                    stackMember.PlanarParts[
                                        secondPhysicalPartIndex]),
                                DescribeRouteSolid(intersectingFirstSolid),
                                DescribeRouteSolid(intersectingSecondSolid));
                            return false;
                        }
                    }
                }

                for (var firstMemberPosition = 0;
                    firstMemberPosition < orderedStackMembers.Count;
                    firstMemberPosition++)
                {
                    for (var secondMemberPosition = firstMemberPosition + 1;
                        secondMemberPosition < orderedStackMembers.Count;
                        secondMemberPosition++)
                    {
                        var firstMember = orderedStackMembers[
                            firstMemberPosition];
                        var secondMember = orderedStackMembers[
                            secondMemberPosition];
                        foreach (var firstMemberSolid in memberSolids[
                            firstMember.MemberIndex])
                        {
                            foreach (var secondMemberSolid in memberSolids[
                                secondMember.MemberIndex])
                            {
                                if (LinkageRouteSolidDistance
                                    .HasPositiveVolumeIntersection(
                                        firstMemberSolid,
                                        secondMemberSolid,
                                        acceptanceTolerance))
                                {
                                    intersectionDiagnostic = string.Format(
                                        CultureInfo.InvariantCulture,
                                        "组 {0} 的最终输出实体成员 {1} 与成员 {2} " +
                                        "在层位回归调整后仍存在正体积重叠；前成员={3}；后成员={4}。",
                                        groupSolution.GroupKey ?? "<未命名组>",
                                        firstMember.MemberIndex + 1,
                                        secondMember.MemberIndex + 1,
                                        string.Join(" / ", firstMember.PlanarParts.Select(DescribePlanarSetting)),
                                        string.Join(" / ", secondMember.PlanarParts.Select(DescribePlanarSetting)));
                                    return false;
                                }
                            }
                        }
                    }
                }

                var accessorySettings = (groupSolution.Accessories ??
                        new List<LinkageRouteAccessory>())
                    .Where(routeAccessory =>
                        routeAccessory?.Setting?.PointList?.Count >= 2)
                    .Select((routeAccessory, accessoryIndex) => new
                    {
                        AccessoryIndex = accessoryIndex,
                        routeAccessory.Kind,
                        Solids = BuildPhysicalPlanarSettingSolids(
                            groupSolution.GroupKey,
                            new[] { routeAccessory.Setting },
                            groupSolution.GroupKey)
                    })
                    .ToList();
                foreach (var accessorySetting in accessorySettings)
                {
                    foreach (var stackMember in orderedStackMembers)
                    {
                        if (!accessorySetting.Solids.Any(accessorySolid =>
                                memberSolids[stackMember.MemberIndex].Any(
                                    memberSolid => LinkageRouteSolidDistance
                                        .HasPositiveVolumeIntersection(
                                            accessorySolid,
                                            memberSolid,
                                            acceptanceTolerance))))
                        {
                            continue;
                        }

                        intersectionDiagnostic = string.Format(
                            CultureInfo.InvariantCulture,
                            "组 {0} 的最终输出附件 {1}({2}) 与叠排成员 {3} " +
                            "在层位回归调整后仍存在正体积重叠。",
                            groupSolution.GroupKey ?? "<未命名组>",
                            accessorySetting.AccessoryIndex + 1,
                            accessorySetting.Kind,
                            stackMember.MemberIndex + 1);
                        return false;
                    }
                }

                for (var firstAccessoryIndex = 0;
                    firstAccessoryIndex < accessorySettings.Count;
                    firstAccessoryIndex++)
                {
                    for (var secondAccessoryIndex = firstAccessoryIndex + 1;
                        secondAccessoryIndex < accessorySettings.Count;
                        secondAccessoryIndex++)
                    {
                        if (!accessorySettings[firstAccessoryIndex].Solids.Any(
                                firstSolid => accessorySettings[
                                    secondAccessoryIndex].Solids.Any(
                                    secondSolid => LinkageRouteSolidDistance
                                        .HasPositiveVolumeIntersection(
                                            firstSolid,
                                            secondSolid,
                                            acceptanceTolerance))))
                        {
                            continue;
                        }

                            intersectionDiagnostic = string.Format(
                                CultureInfo.InvariantCulture,
                                "组 {0} 的最终输出附件 {1}({2}) 与附件 {3}({4}) 在层位回归调整后仍存在正体积重叠。",
                                groupSolution.GroupKey ?? "<未命名组>",
                                accessorySettings[firstAccessoryIndex]
                                    .AccessoryIndex + 1,
                                accessorySettings[firstAccessoryIndex].Kind,
                                accessorySettings[secondAccessoryIndex]
                                    .AccessoryIndex + 1,
                                accessorySettings[secondAccessoryIndex].Kind);
                        return false;
                    }
                }
            }

            return true;
        }

        private static string DescribePlanarSetting(
            PlanarBendBusbarSetting planarSetting)
        {
            if (planarSetting?.PointList == null)
            {
                return "<空>";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "规格{0:F3}×{1:F3},逻辑段{2},点[{3}]",
                planarSetting.BusBarWidth,
                planarSetting.BusBarThickness,
                planarSetting.SpecificationSectionIndex,
                string.Join("→", planarSetting.PointList.Select(point =>
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "({0:F3},{1:F3},{2:F3})",
                        point.X,
                        point.Y,
                        point.Z))));
        }

        private static string DescribeRouteSolid(LinkageRouteSolid routeSolid)
        {
            if (routeSolid == null)
            {
                return "<空>";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "段{0}/心({1:F6},{2:F6},{3:F6})/长轴({4:F6},{5:F6},{6:F6})/{7:F6}/宽轴({8:F6},{9:F6},{10:F6})/{11:F6}/厚轴({12:F6},{13:F6},{14:F6})/{15:F6}",
                routeSolid.SegmentIndex,
                routeSolid.Center.X,
                routeSolid.Center.Y,
                routeSolid.Center.Z,
                routeSolid.LengthDirection.X,
                routeSolid.LengthDirection.Y,
                routeSolid.LengthDirection.Z,
                routeSolid.HalfLength,
                routeSolid.WidthDirection.X,
                routeSolid.WidthDirection.Y,
                routeSolid.WidthDirection.Z,
                routeSolid.HalfWidth,
                routeSolid.ThicknessDirection.X,
                routeSolid.ThicknessDirection.Y,
                routeSolid.ThicknessDirection.Z,
                routeSolid.HalfThickness);
        }

        // 仅在完整候选的输出边界拆U。转换器先在局部副本上完成全部组，
        // 任一组拆分失败不回写，原搜索候选继续保持U开启时的几何和排序。
        // 此处不授予发布资格：随后仍运行联动、补块、全部最终实体门禁。
        private static bool TryApplyUShapeOutputPolicy(
            LinkageMultiGroupRouteRequest outputRequest,
            LinkageRouteSolution outputSolution,
            out bool geometryChanged,
            out string failureReason)
        {
            geometryChanged = false;
            failureReason = null;
            if (outputRequest == null || outputSolution?.GroupSolutions == null)
            {
                failureReason = "最终U型转换缺少完整输出请求或方案。";
                return false;
            }
            if (outputRequest.AllowUShapeRoutes)
                return true;
            var preparedOutputConversions = new List<KeyValuePair<LinkageGroupRouteSolution,
                LinkageRouteCenterlineConversionResult>>();
            foreach (var outputGroup in outputSolution.GroupSolutions)
            {
                var hasOutputU = LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(outputGroup.PlanarParts) ||
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(outputGroup.StackMembers
                        .Where(member => member?.PlanarParts != null).SelectMany(member => member.PlanarParts));
                if (!hasOutputU)
                    continue;
                var outputConversion = CreateOutputAdjustmentConversion(outputGroup);
                if (!LinkageRouteUShapeOutputTransformer.TryReplaceUShapes(outputConversion,
                        out var groupGeometryChanged, out failureReason) || !groupGeometryChanged ||
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(outputConversion.CenterlineParts) ||
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(outputConversion.StackMembers
                        .SelectMany(member => member.PlanarParts)))
                {
                    failureReason = "最终禁用U型转换失败（" + outputGroup.GroupKey + "）：" + failureReason;
                    return false;
                }
                // 内部旧垫块在新增物理接口后不再权威。完整联动末轮重新补齐；
                // 固定端附件保留实体且独立复制，禁止把原候选附件就地改写。
                var outputAccessories = outputConversion.Accessories
                    .Where(accessory => accessory.Kind != LinkageRouteAccessoryKind.SupportBlock ||
                        string.IsNullOrWhiteSpace(accessory.SupportBlockKey))
                    .Select(accessory => new LinkageRouteAccessory
                    {
                        Kind = accessory.Kind, TerminalSide = accessory.TerminalSide,
                        RelatedGeneratedMemberIndex = accessory.RelatedGeneratedMemberIndex,
                        SupportBlockKey = accessory.SupportBlockKey, Setting = accessory.Setting?.Copy()
                    }).ToList();
                outputConversion.Accessories.Clear();
                foreach (var outputAccessory in outputAccessories)
                    outputConversion.Accessories.Add(outputAccessory);
                preparedOutputConversions.Add(new KeyValuePair<LinkageGroupRouteSolution,
                    LinkageRouteCenterlineConversionResult>(outputGroup, outputConversion));
            }
            foreach (var preparedOutput in preparedOutputConversions)
            {
                preparedOutput.Key.PlanarParts = preparedOutput.Value.CenterlineParts;
                preparedOutput.Key.StackMembers = preparedOutput.Value.StackMembers;
                preparedOutput.Key.Accessories = preparedOutput.Value.Accessories;
                preparedOutput.Key.DoubleStartPhysicalBridgeSectionIndices.Clear();
                foreach (var expandedBridgeIndex in preparedOutput.Value.DoubleStartPhysicalBridgeSectionIndices)
                    preparedOutput.Key.DoubleStartPhysicalBridgeSectionIndices.Add(expandedBridgeIndex);
                AddOutputAdjustmentDiagnostic(outputSolution, preparedOutput.Key.GroupKey,
                    "M/U长度限制已转换为L或实际满搭接双L，等待全部最终实体门禁。");
            }
            geometryChanged = preparedOutputConversions.Count > 0;
            if (geometryChanged)
                LinkageRouteCopperLength.Recalculate(outputRequest, outputSolution);
            return true;
        }

        private static void ApplyPhysicalSolutionOutputRegressionAdjustments(
            LinkageRouteSolution routeSolution,
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCenterlineConversionResult>
                sourceConversions = null)
        {
            if (routeSolution?.GroupSolutions == null)
            {
                return;
            }

            var adjustmentConversions = sourceConversions?.ToList() ??
                routeSolution.GroupSolutions
                    .Select(CreateOutputAdjustmentConversion)
                    .Where(conversionResult => conversionResult != null)
                    .ToList();
            var deferredInternalSupportBlocks = adjustmentConversions
                .Select(adjustmentConversion => adjustmentConversion.Accessories
                    .Where(routeAccessory =>
                        routeAccessory?.Kind ==
                            LinkageRouteAccessoryKind.SupportBlock &&
                        !string.IsNullOrWhiteSpace(
                            routeAccessory.SupportBlockKey))
                    .ToList())
                .ToList();
            for (var groupSolutionIndex = 0;
                groupSolutionIndex < routeSolution.GroupSolutions.Count &&
                    groupSolutionIndex < adjustmentConversions.Count;
                groupSolutionIndex++)
            {
                var adjustmentConversion = adjustmentConversions[
                    groupSolutionIndex];
                foreach (var deferredInternalSupportBlock in
                    deferredInternalSupportBlocks[groupSolutionIndex])
                {
                    adjustmentConversion.Accessories.Remove(
                        deferredInternalSupportBlock);
                }

                // 厚差垫块必须等所有实体排的搭接段位置稳定后再生成。
                // 回归循环内先从结构复查中移除旧内部垫块，避免垫块反向
                // 驱动铜排偏移或把本来可修正的实体路径卡在旧缝隙上。
                routeSolution.GroupSolutions[groupSolutionIndex].Accessories =
                    adjustmentConversion.Accessories.ToList();
            }

            for (var adjustmentIndex = 0;
                adjustmentIndex < 10;
                adjustmentIndex++)
            {
                var anyGeometryAdjustedThisRound = false;
                for (var groupSolutionIndex = 0;
                    groupSolutionIndex < routeSolution.GroupSolutions.Count &&
                        groupSolutionIndex < adjustmentConversions.Count;
                    groupSolutionIndex++)
                {
                    var groupSolution = routeSolution.GroupSolutions[
                        groupSolutionIndex];
                    var adjustmentConversion = adjustmentConversions[
                        groupSolutionIndex];
                    var groupRequest = routeRequest?.Groups?.FirstOrDefault(
                        currentGroupRequest => string.Equals(
                            currentGroupRequest?.GroupKey,
                            groupSolution.GroupKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (groupRequest?.StartTerminal?.Members?.Count == 1 &&
                        !(routeRequest.EnableSingleStartConnectionBar &&
                          adjustmentConversion.Accessories.Any(accessory =>
                              accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)))
                    {
                        var singleStartAdjustmentSucceeded =
                            LinkageRouteTerminalAccessoryBuilder
                                .TryAdjustSingleStartHorizontalLapLayersForOutput(
                                    adjustmentConversion,
                                    groupRequest.StartTerminal,
                                    groupRequest.StackStartSpace,
                                    routeRequest?.RequireSingleStartMiddleLap == true,
                                    out var singleStartGeometryAdjusted,
                                    out var singleStartAdjustmentFailureReason);
                        if (!singleStartAdjustmentSucceeded)
                        {
                            AddOutputAdjustmentDiagnostic(
                                routeSolution,
                                groupSolution.GroupKey,
                                singleStartAdjustmentFailureReason);
                        }
                        else if (singleStartGeometryAdjusted)
                        {
                            anyGeometryAdjustedThisRound = true;
                            var sourceAccessories = adjustmentConversion
                                .Accessories.ToList();
                            if (!LinkageRouteTerminalAccessoryBuilder.TryBuild(
                                    adjustmentConversion,
                                    routeRequest,
                                    groupRequest,
                                    out var accessoryRebuildFailureReason,
                                    true))
                            {
                                adjustmentConversion.Accessories.Clear();
                                foreach (var sourceAccessory in sourceAccessories)
                                {
                                    adjustmentConversion.Accessories.Add(
                                        sourceAccessory);
                                }
                                AddOutputAdjustmentDiagnostic(
                                    routeSolution,
                                    groupSolution.GroupKey,
                                    "单始端重叠校正后附件重建失败，已恢复原附件：" +
                                    accessoryRebuildFailureReason);
                            }
                        }
                    }
                    // 连接排端的外部搭接只在进入回归时锁定一次。后续轮次
                    // 必须以上一轮已经移动的内部搭接段继续联动，不能每轮
                    // 重新套用初始连接排层位，否则会在两个冲突层位间循环。
                    if (adjustmentIndex == 0 &&
                        !LinkageRouteSectionSpecificationPostProcessor
                            .TryAlignPhysicalBridgeFollowingLapEndpoints(
                                adjustmentConversion,
                                groupRequest?.StartTerminal,
                                out var physicalBridgeAdjustmentFailureReason) &&
                        !string.IsNullOrWhiteSpace(
                            physicalBridgeAdjustmentFailureReason) &&
                        !adjustmentConversion.Diagnostics.Contains(
                            physicalBridgeAdjustmentFailureReason))
                    {
                        adjustmentConversion.Diagnostics.Add(
                            physicalBridgeAdjustmentFailureReason);
                    }

                    // 每轮先恢复多始端连接排侧逐层贴合。中间搭接段在
                    // 上一轮的联动移动可能反向影响始端段，因此不能只在
                    // 第一轮执行，也不能以“当前无干涉”作为提前退出条件。
                    LinkageRouteTerminalAccessoryBuilder
                        .ApplyOutputTerminalBridgeLayerAlignment(
                            adjustmentConversion,
                            groupRequest?.StartTerminal);

                    var sectionLapAdjustmentSucceeded =
                        LinkageRouteSectionSpecificationPostProcessor
                            .TrySeparateNonAdjacentPhysicalLapSegments(
                                adjustmentConversion,
                                out var sectionLapGeometryAdjusted,
                                out var sectionLapAdjustmentFailureReason);
                    if (!string.IsNullOrWhiteSpace(
                            sectionLapAdjustmentFailureReason))
                    {
                        AddOutputAdjustmentDiagnostic(
                            routeSolution,
                            groupSolution.GroupKey,
                            sectionLapAdjustmentFailureReason);
                    }
                    var internalGapAdjustmentSucceeded =
                        LinkageRouteSectionSpecificationPostProcessor
                            .TrySeparateInternalPhysicalLapGaps(
                                adjustmentConversion,
                                routeRequest,
                                groupRequest,
                                out var internalGapGeometryAdjusted,
                                out var internalGapAdjustmentFailureReason);
                    anyGeometryAdjustedThisRound |= internalGapGeometryAdjusted;
                    if (!string.IsNullOrWhiteSpace(
                            internalGapAdjustmentFailureReason))
                    {
                        AddOutputAdjustmentDiagnostic(
                            routeSolution,
                            groupSolution.GroupKey,
                            internalGapAdjustmentFailureReason);
                    }

                    // 即使本轮仍有其他搭接尚未完全闭合，前面的厚向校正
                    // 也可能已经重建点列；N 形稳定不能被后续 continue
                    // 跳过，否则最后一轮会把已延长的转折恢复成原交叉点。
                    LinkageRouteSectionSpecificationPostProcessor
                        .TryStabilizePersistentNTransitions(
                            adjustmentConversion,
                            out var persistentNTransitionAdjusted);
                    anyGeometryAdjustedThisRound |=
                        persistentNTransitionAdjusted;

                    if (!sectionLapAdjustmentSucceeded)
                    {
                        continue;
                    }
                    if (!internalGapAdjustmentSucceeded)
                    {
                        continue;
                    }

                    anyGeometryAdjustedThisRound |= sectionLapGeometryAdjusted;

                    groupSolution.Accessories = adjustmentConversion.Accessories
                        .ToList();
                }

                var physicalStructureIsValid =
                    TryValidatePhysicalSolutionStructure(routeSolution, out _);
                var terminalBridgeLayersAreValid = true;
                var internalLapsAreValidOrAwaitingFinalSupport = true;
                for (var groupSolutionIndex = 0;
                    groupSolutionIndex < adjustmentConversions.Count;
                    groupSolutionIndex++)
                {
                    var adjustmentConversion = adjustmentConversions[
                        groupSolutionIndex];
                    if (!LinkageRouteTerminalAccessoryBuilder
                            .TryValidateOutputTerminalBridgeLayerAlignment(
                                adjustmentConversion,
                                out _))
                    {
                        terminalBridgeLayersAreValid = false;
                    }

                    var groupRequest = routeRequest?.Groups?.FirstOrDefault(
                        currentGroupRequest => string.Equals(
                            currentGroupRequest?.GroupKey,
                            routeSolution.GroupSolutions[groupSolutionIndex]
                                .GroupKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (groupRequest != null &&
                        !LinkageRouteTerminalAccessoryBuilder
                            .TryValidateSingleStartHorizontalLapLayersForOutput(
                                adjustmentConversion,
                                groupRequest.StartTerminal,
                                routeRequest?.RequireSingleStartMiddleLap == true,
                                out _))
                    {
                        terminalBridgeLayersAreValid = false;
                    }

                    if (!LinkageRouteSectionSpecificationPostProcessor
                            .TryValidateInternalPhysicalLapClosure(
                                adjustmentConversion,
                                false,
                                out var requiresFinalSupportBlock,
                                out _) &&
                        !requiresFinalSupportBlock)
                    {
                        internalLapsAreValidOrAwaitingFinalSupport = false;
                    }
                }

                if (!anyGeometryAdjustedThisRound &&
                    physicalStructureIsValid &&
                    terminalBridgeLayersAreValid &&
                    internalLapsAreValidOrAwaitingFinalSupport)
                {
                    break;
                }
            }

            // 柜顶端直段本身可能已正确贴面，但其前一斜过渡穿入固定排。
            // 仅沿末端直腿后移过渡折点；两端位置和最终实体门禁保持不变。
            for (var groupSolutionIndex = 0;
                groupSolutionIndex < routeSolution.GroupSolutions.Count &&
                    groupSolutionIndex < adjustmentConversions.Count;
                groupSolutionIndex++)
            {
                var groupSolution = routeSolution.GroupSolutions[groupSolutionIndex];
                var groupRequest = routeRequest?.Groups?.FirstOrDefault(currentGroupRequest =>
                    string.Equals(currentGroupRequest?.GroupKey, groupSolution.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                TryRetreatFinalTerminalTransitionFromFixedBars(
                    adjustmentConversions[groupSolutionIndex], groupRequest,
                    out _);

                // 十轮内部搭接联动和柜顶过渡回退均完成后，以当前确有
                // 搭接的主排端面收齐其它叠排端头，再复核端部折弯余量。
                // 两者不能放回联动循环内，否则下一轮会重新拉开端面。
                LinkageRouteSectionSpecificationPostProcessor
                    .AlignFinalTerminalLapEndpointsToPrimaryMembers(
                        adjustmentConversions[groupSolutionIndex],
                        routeRequest, groupRequest);
                if (!LinkageRouteSectionSpecificationPostProcessor
                    .TryValidateFinalTerminalPerpendicularLapBendReserves(
                        routeRequest, groupRequest, groupSolution, out _) &&
                    !LinkageRouteSectionSpecificationPostProcessor
                        .TryEnsureFinalTerminalPerpendicularLapBendReservesFromExistingGeometry(
                            adjustmentConversions[groupSolutionIndex],
                            routeRequest, groupRequest,
                            out var finalTerminalReserveFailureReason))
                {
                    AddOutputAdjustmentDiagnostic(routeSolution,
                        groupSolution.GroupKey,
                        "最终端腿余量调整无法复用实际物理分段：" +
                        finalTerminalReserveFailureReason);
                }
            }

            // 十轮联动后再按当前实际成员的圆弧切点补齐；先铜排，最后垫块。
            for (var roundedGroupIndex = 0; roundedGroupIndex < routeSolution.GroupSolutions.Count &&
                roundedGroupIndex < adjustmentConversions.Count; roundedGroupIndex++)
            {
                var roundedGroup = routeSolution.GroupSolutions[roundedGroupIndex];
                var roundedRequest = routeRequest.Groups.FirstOrDefault(item => string.Equals(item.GroupKey,
                    roundedGroup.GroupKey, StringComparison.OrdinalIgnoreCase));
                if (!LinkageRouteSectionSpecificationPostProcessor.TryRepairRoundedLapInterfaces(
                        adjustmentConversions[roundedGroupIndex], routeRequest, roundedRequest,
                        () => TryValidatePhysicalSolutionStructure(routeSolution, out _,
                                  LinkageRouteGeometryTolerances.FinalAcceptanceComparison) &&
                              TryValidatePhysicalSolutionStaticRestrictions(routeRequest, routeSolution, out _),
                        out var roundedFailure))
                    AddOutputAdjustmentDiagnostic(routeSolution, roundedGroup.GroupKey, "真实圆弧搭接修复失败：" + roundedFailure);
                if (!LinkageRouteSectionSpecificationPostProcessor.TryReconcileAdjustedRoundedLapAllowances(
                        adjustmentConversions[roundedGroupIndex], routeRequest, roundedRequest, out var adjustedAllowanceFailure))
                    AddOutputAdjustmentDiagnostic(routeSolution, roundedGroup.GroupKey, "调整后真实圆弧验收失败：" + adjustedAllowanceFailure);
            }

            // 实体排完成最多 10 轮联动偏移后，才按最终搭接面一次性补齐
            // 厚差缝隙。垫块生成失败只回滚垫块附件并记录错误，不再改变
            // 已经稳定的铜排位置，也不淘汰普通搜索方案。
            for (var groupSolutionIndex = 0;
                groupSolutionIndex < routeSolution.GroupSolutions.Count &&
                    groupSolutionIndex < adjustmentConversions.Count;
                groupSolutionIndex++)
            {
                var groupSolution = routeSolution.GroupSolutions[
                    groupSolutionIndex];
                var adjustmentConversion = adjustmentConversions[
                    groupSolutionIndex];
                var groupRequest = routeRequest?.Groups?.FirstOrDefault(
                    currentGroupRequest => string.Equals(
                        currentGroupRequest?.GroupKey,
                        groupSolution.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                var requiresInternalSupportRebuild = groupRequest != null &&
                    adjustmentConversion.CenterlineParts.Count > 1;
                groupSolution.InternalSupportRebuildFailed = false;
                if (requiresInternalSupportRebuild)
                {
                    LinkageRouteSectionSpecificationPostProcessor.ProcessingState
                        sectionSpecificationProcessingState = null;
                    string sectionProcessingStateFailureReason = null;
                    var processingStateCreated =
                        LinkageRouteSectionSpecificationPostProcessor
                            .TryCreateProcessingStateFromExistingGeometry(
                                adjustmentConversion,
                                out sectionSpecificationProcessingState,
                                out sectionProcessingStateFailureReason);
                    var internalSupportBlockFailureReason = string.Empty;
                    var internalSupportBlocksRebuilt = processingStateCreated &&
                        LinkageRouteSectionSpecificationPostProcessor
                            .TryAddInternalSupportBlocks(
                                adjustmentConversion,
                                routeRequest,
                                groupRequest,
                                sectionSpecificationProcessingState,
                                out internalSupportBlockFailureReason);
                    if (!internalSupportBlocksRebuilt)
                    {
                        groupSolution.InternalSupportRebuildFailed = true;
                        RemoveInternalSupportBlocks(adjustmentConversion);
                        foreach (var deferredInternalSupportBlock in
                            deferredInternalSupportBlocks[groupSolutionIndex])
                        {
                            adjustmentConversion.Accessories.Add(
                                deferredInternalSupportBlock);
                        }
                        AddOutputAdjustmentDiagnostic(
                            routeSolution,
                            groupSolution.GroupKey,
                            processingStateCreated
                                ? internalSupportBlockFailureReason
                                : sectionProcessingStateFailureReason);
                    }

                    var supportTrimSucceeded =
                        LinkageRouteSectionSpecificationPostProcessor
                            .TryTrimInternalSupportBlocksAroundPhysicalIntersections(
                                adjustmentConversion,
                                out _,
                                out var supportTrimFailureReason);
                    if (!string.IsNullOrWhiteSpace(supportTrimFailureReason))
                    {
                        AddOutputAdjustmentDiagnostic(
                            routeSolution,
                            groupSolution.GroupKey,
                            supportTrimFailureReason);
                    }
                    if (!supportTrimSucceeded)
                    {
                        groupSolution.InternalSupportRebuildFailed = true;
                        AddOutputAdjustmentDiagnostic(
                            routeSolution,
                            groupSolution.GroupKey,
                            "最终垫块裁剪未完成，已保留当前实体并继续最终绝缘距离复核。" );
                    }
                }

                if (!LinkageRouteTerminalAccessoryBuilder.TryRebuildFinalStartBridgeSupportBlocks(
                        adjustmentConversion, routeRequest, groupRequest, out var finalStartSupportFailure))
                {
                    groupSolution.InternalSupportRebuildFailed = true;
                    AddOutputAdjustmentDiagnostic(routeSolution, groupSolution.GroupKey, finalStartSupportFailure);
                }
                LinkageRouteSupportBlockLayers.SplitOversized(adjustmentConversion);
                groupSolution.Accessories = adjustmentConversion.Accessories
                    .ToList();
                groupSolution.ActualCopperLength = groupSolution.StackMembers
                        .Where(stackMember => stackMember?.PlanarParts != null)
                        .SelectMany(stackMember => stackMember.PlanarParts)
                        .Where(planarSetting => planarSetting != null)
                        .Sum(planarSetting =>
                            planarSetting.GetDevelopedLength()) +
                    LinkageRouteCopperLength.GetBridgeLength(groupSolution.Accessories);

                if (!LinkageRouteTerminalAccessoryBuilder
                        .TryValidateOutputTerminalBridgeLayerAlignment(
                            adjustmentConversion,
                            out var terminalLapClosureFailureReason))
                {
                    AddOutputAdjustmentDiagnostic(
                        routeSolution,
                        groupSolution.GroupKey,
                        "最终多始端连接排搭接复查失败：" +
                        terminalLapClosureFailureReason);
                }

                if (groupRequest != null &&
                    !LinkageRouteTerminalAccessoryBuilder
                        .TryValidateSingleStartHorizontalLapLayersForOutput(
                            adjustmentConversion,
                            groupRequest.StartTerminal,
                            routeRequest?.RequireSingleStartMiddleLap == true,
                            out var singleStartLapFailureReason))
                {
                    AddOutputAdjustmentDiagnostic(
                        routeSolution,
                        groupSolution.GroupKey,
                        "最终单始端搭接复查失败：" +
                        singleStartLapFailureReason);
                }

                if (!LinkageRouteSectionSpecificationPostProcessor
                        .TryValidateInternalPhysicalLapClosure(
                            adjustmentConversion,
                            true,
                            out _,
                            out var internalLapClosureFailureReason))
                {
                    AddOutputAdjustmentDiagnostic(
                        routeSolution,
                        groupSolution.GroupKey,
                        "最终内部搭接复查失败：" +
                        internalLapClosureFailureReason);
                }
            }

            LinkageRouteCopperLength.Recalculate(routeRequest, routeSolution);

            AppendPhysicalSolutionStructureDiagnostic(routeSolution);
        }

        private static bool TryRetreatFinalTerminalTransitionFromFixedBars(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageRouteGroupRequest routeGroupRequest,
            out bool geometryAdjusted)
        {
            geometryAdjusted = false;
            var fixedTerminalSettings = routeGroupRequest?.EndTerminal?.Members?
                .Select(member => member?.FixedBusbar)
                .Where(setting => setting?.PointList?.Count >= 2)
                .ToList();
            if (fixedTerminalSettings == null || fixedTerminalSettings.Count == 0 ||
                conversionResult?.StackMembers == null)
                return true;

            foreach (var stackMember in conversionResult.StackMembers)
            {
                var terminalSetting = stackMember?.PlanarParts?.LastOrDefault();
                if (terminalSetting?.PointList?.Count < 4)
                    continue;
                if (!LinkageRouteSectionSpecificationPostProcessor
                        .TryMoveLapTransitionBeyondIntersections(
                            terminalSetting, false, fixedTerminalSettings,
                            null, 0d, out var retreatDistance))
                    return false;
                geometryAdjusted |= retreatDistance >
                    LinkageRouteGeometryTolerances.PointCoincidence;
            }
            return true;
        }

        private static void RemoveInternalSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult)
        {
            if (conversionResult?.Accessories == null)
            {
                return;
            }

            for (var accessoryIndex = conversionResult.Accessories.Count - 1;
                accessoryIndex >= 0;
                accessoryIndex--)
            {
                var routeAccessory = conversionResult.Accessories[accessoryIndex];
                if (routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.SupportBlock &&
                    !string.IsNullOrWhiteSpace(routeAccessory.SupportBlockKey))
                {
                    conversionResult.Accessories.RemoveAt(accessoryIndex);
                }
            }
        }

        private static void AddOutputAdjustmentDiagnostic(
            LinkageRouteSolution routeSolution,
            string groupKey,
            string message)
        {
            if (routeSolution == null || string.IsNullOrWhiteSpace(message) ||
                routeSolution.Diagnostics.Any(diagnostic =>
                    string.Equals(
                        diagnostic?.GroupKey,
                        groupKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        diagnostic?.Message,
                        message,
                        StringComparison.Ordinal)))
            {
                return;
            }

            routeSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
            {
                GroupKey = groupKey,
                Stage = LinkageRouteFailureStage.Conversion,
                Message = message
            });
        }

        private static LinkageRouteCenterlineConversionResult
            CreateOutputAdjustmentConversion(
            LinkageGroupRouteSolution groupSolution)
        {
            if (groupSolution == null)
            {
                return null;
            }

            var conversionResult =
                new LinkageRouteCenterlineConversionResult { USplitContactSide = groupSolution.USplitContactSide };
            foreach (var centerlinePart in groupSolution.PlanarParts ??
                new List<PlanarBendBusbarSetting>())
            {
                conversionResult.CenterlineParts.Add(centerlinePart);
            }
            foreach (var stackMember in groupSolution.StackMembers ??
                new List<LinkageStackedRouteMember>())
            {
                conversionResult.StackMembers.Add(stackMember);
                conversionResult.StartTerminalMemberIndices.Add(
                    stackMember.MemberIndex);
            }

            foreach (var routeAccessory in groupSolution.Accessories ??
                new List<LinkageRouteAccessory>())
            {
                conversionResult.Accessories.Add(routeAccessory);
            }

            // 结果模型不序列化端部层位对象，但最终门禁仍必须知道双始端
            // 水平搭接应有几根连接排。按实际生成物理叠排数量推导：一根
            // 生成排只保留实际搭接侧一根，两个及以上生成排保留两侧两根。
            // 不能因为输出转换没有 StartStackPlacement 就跳过数量校验。
            var outputBridgeAccessories = conversionResult.Accessories
                .Where(routeAccessory => routeAccessory?.Kind ==
                    LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)
                .ToList();
            if (outputBridgeAccessories.Count > 0 &&
                conversionResult.StackMembers.Count > 0)
            {
                var expectedOutputBridgeMemberIndices = groupSolution
                    .ExpectedDoubleStartBridgeMemberIndices;
                var expectedOutputBridgeCount =
                    expectedOutputBridgeMemberIndices.Count > 0
                        ? expectedOutputBridgeMemberIndices.Count
                        : Math.Min(
                            2,
                            Math.Max(1, conversionResult.StackMembers.Count));
                var outputBridgeThickness = outputBridgeAccessories[0].Setting
                    ?.BusBarThickness ?? 0d;
                var outputBridgePlacements = outputBridgeAccessories
                    .Select(routeAccessory =>
                        new LinkageRouteTerminalBridgePlacement
                        {
                            GeneratedMemberIndex = routeAccessory
                                .RelatedGeneratedMemberIndex,
                            SideSign = 1,
                            Thickness = outputBridgeThickness
                        })
                    .ToList();
                if (outputBridgePlacements.Count == 1 &&
                    expectedOutputBridgeCount == 2)
                {
                    var primaryMemberIndex = outputBridgePlacements[0]
                        .GeneratedMemberIndex;
                    var oppositeMemberIndex = expectedOutputBridgeMemberIndices
                        .FirstOrDefault(memberIndex =>
                            memberIndex != primaryMemberIndex);
                    if (!expectedOutputBridgeMemberIndices.Contains(
                            oppositeMemberIndex))
                    {
                        oppositeMemberIndex = primaryMemberIndex == 0
                            ? conversionResult.StackMembers.Count - 1
                            : 0;
                    }
                    outputBridgePlacements.Add(
                        new LinkageRouteTerminalBridgePlacement
                        {
                            GeneratedMemberIndex = oppositeMemberIndex,
                            SideSign = -1,
                            Thickness = outputBridgeThickness
                        });
                }

                conversionResult.StartStackPlacement =
                    new LinkageRouteStackTerminalPlacement
                    {
                        GeneratedMemberOffsets = Enumerable.Repeat(
                            0d,
                            conversionResult.StackMembers.Count).ToList(),
                        DoubleStartHorizontalBridgePlacements =
                            outputBridgePlacements
                    };
            }
            foreach (var physicalSectionIndex in groupSolution
                .DoubleStartPhysicalBridgeSectionIndices)
            {
                conversionResult.DoubleStartPhysicalBridgeSectionIndices.Add(
                    physicalSectionIndex);
            }

            return conversionResult;
        }

        private static void AppendPhysicalSolutionStructureDiagnostic(
            LinkageRouteSolution routeSolution)
        {
            if (routeSolution == null ||
                TryValidatePhysicalSolutionStructure(
                    routeSolution,
                    out var intersectionDiagnostic) ||
                string.IsNullOrWhiteSpace(intersectionDiagnostic) ||
                routeSolution.Diagnostics.Any(diagnostic =>
                    diagnostic?.Message?.IndexOf(
                        intersectionDiagnostic,
                        StringComparison.Ordinal) >= 0))
            {
                return;
            }

            routeSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
            {
                Stage = LinkageRouteFailureStage.Conversion,
                Message = "最终输出连续 10 轮联动回归调整后仍未消除" +
                    "正体积干涉；当前候选仅保留错误诊断，最终发布门禁必须淘汰：" +
                    intersectionDiagnostic
            });
        }

        private static List<LinkageRouteSolid> BuildPhysicalPlanarSettingSolids(
            string groupKey,
            IEnumerable<PlanarBendBusbarSetting> planarSettings,
            string clearanceGroupKey)
        {
            var generatedSolids = new List<LinkageRouteSolid>();
            var solidIndex = 0;
            foreach (var planarSetting in planarSettings ??
                Array.Empty<PlanarBendBusbarSetting>())
            {
                if (planarSetting?.PointList == null ||
                    planarSetting.PointList.Count < 2 ||
                    planarSetting.WidthVector == null ||
                    planarSetting.BusBarWidth <= 0d ||
                    planarSetting.BusBarThickness <= 0d)
                {
                    continue;
                }

                var widthDirection = planarSetting.WidthVector.Normalized;
                for (var pointIndex = 0;
                    pointIndex < planarSetting.PointList.Count - 1;
                    pointIndex++)
                {
                    var segmentStart = planarSetting.PointList[pointIndex];
                    var segmentEnd = planarSetting.PointList[pointIndex + 1];
                    if (segmentStart == null || segmentEnd == null)
                    {
                        continue;
                    }

                    var segmentDirection = new Vector3d(
                        segmentStart,
                        segmentEnd);
                    if (segmentDirection.Norm <= 1e-8d ||
                        widthDirection == null)
                    {
                        continue;
                    }

                    var thicknessDirection = segmentDirection
                        .Cross(widthDirection).Normalized;
                    if (thicknessDirection == null)
                    {
                        continue;
                    }

                    generatedSolids.Add(LinkageRouteSolidGeometry.CreateSegmentSolid(
                        groupKey,
                        segmentStart,
                        segmentEnd,
                        widthDirection,
                        thicknessDirection,
                        planarSetting.BusBarWidth,
                        planarSetting.BusBarThickness,
                        solidIndex++,
                        clearanceGroupKey));
                }
            }

            return generatedSolids;
        }

        private static bool IsSameClearanceGroup(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid)
        {
            return firstSolid != null && secondSolid != null &&
                !string.IsNullOrWhiteSpace(firstSolid.ClearanceGroupKey) &&
                string.Equals(
                    firstSolid.ClearanceGroupKey,
                    secondSolid.ClearanceGroupKey,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<LinkageRouteSolution> SolveCore(
            LinkageMultiGroupRouteRequest requestedRouteRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            return SolveCore(
                requestedRouteRequest,
                requestedRouteRequest,
                algorithmKind,
                progress,
                cancellationToken);
        }

        private static IReadOnlyList<LinkageRouteSolution> SolveCore(
            LinkageMultiGroupRouteRequest requestedRouteRequest,
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            return SolveCore(requestedRouteRequest, outputRouteRequest, algorithmKind,
                progress, cancellationToken, null);
        }

        private static IReadOnlyList<LinkageRouteSolution> SolveCore(
            LinkageMultiGroupRouteRequest requestedRouteRequest,
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken,
            Action finiteSeedStageCompleted)
        {
            outputRouteRequest = outputRouteRequest ?? requestedRouteRequest;
            var routeRequest = requestedRouteRequest.CreatePathSearchRequest();
            var searchStopwatch = Stopwatch.StartNew();
            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedTerminalAndObstacleSolids,
                    out var staticSolidFailureDiagnostic))
            {
                var inputFailureSolution = new LinkageRouteSolution
                {
                    AlgorithmKind = algorithmKind,
                    IsSuccess = false,
                    RequestedInsulationClearance =
                        outputRouteRequest?.InsulationClearance ?? 0d,
                    RequestedLapInsulationClearance =
                        outputRouteRequest?.LapInsulationClearance ?? 35d,
                    SearchInsulationClearance =
                        routeRequest?.InsulationClearance ?? 0d
                };
                inputFailureSolution.Diagnostics.Add(
                    new LinkageRouteFailureDiagnostic
                    {
                        Stage = LinkageRouteFailureStage.Input,
                        Message = staticSolidFailureDiagnostic ??
                            "固定端、整体禁区或障碍物实体构建失败。"
                    });
                progress?.Report(new LinkageRouteProgress
                {
                    AlgorithmKind = algorithmKind,
                    ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                    Message = inputFailureSolution.Diagnostics[0].Message
                });
                return new[] { inputFailureSolution };
            }

            var searchContext = new SearchContext(
                routeRequest,
                outputRouteRequest,
                algorithmKind,
                fixedTerminalAndObstacleSolids,
                progress,
                searchStopwatch,
                cancellationToken);
            var mainSearchCancellationToken =
                searchContext.MainSearchCancellationToken;
            var combinationSearchInterrupted =
                mainSearchCancellationToken.IsCancellationRequested;
            IReadOnlyList<LinkageRouteCandidatePool> retainedPhysicalFinitePools = null;
            if (!combinationSearchInterrupted &&
                ShouldUseFastSeedStage(routeRequest))
            {
                var fastForwardCandidatePools = BuildFastCandidatePools(
                    routeRequest,
                    fixedTerminalAndObstacleSolids,
                    LinkageRouteTraversalDirection.Forward,
                    mainSearchCancellationToken);
                IReadOnlyList<LinkageRouteCandidatePool> fastCandidatePools =
                    fastForwardCandidatePools;
                var corridorPools = BuildCommonPhaseCorridorPools(searchContext, fastForwardCandidatePools);
                if (corridorPools != null && !mainSearchCancellationToken.IsCancellationRequested)
                {
                    var remainingFirstSolutionMilliseconds = routeRequest.SearchOptions.FirstSolutionTargetMilliseconds -
                        searchStopwatch.ElapsedMilliseconds;
                    if (remainingFirstSolutionMilliseconds > 0)
                    {
                        combinationSearchInterrupted = RunCombinationSearch(
                            searchContext, corridorPools, stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: (int)Math.Min(
                                routeRequest.SearchOptions.FastSearchStageLimitMilliseconds, remainingFirstSolutionMilliseconds),
                            allowClearanceExpansionCandidates: false, exploreAllPoolOrders: false,
                            prioritizeSmallerPools: true);
                    }
                }
                var fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(
                    fastCandidatePools);
                if (!combinationSearchInterrupted && !searchContext.HasSuccessfulSolution &&
                    !fastCandidatePoolsHaveEmptyGroup)
                {
                    combinationSearchInterrupted = RunCombinationSearch(
                        searchContext,
                        fastCandidatePools,
                        stopAfterFirstSuccessfulSolution: true,
                        maximumStageMilliseconds: Math.Max(
                            1,
                            Math.Min(
                                routeRequest.SearchOptions
                                    .FastSearchStageLimitMilliseconds,
                                routeRequest.SearchOptions
                                    .FirstSolutionTargetMilliseconds)),
                        allowClearanceExpansionCandidates: false,
                        exploreAllPoolOrders: false);

                }

                // 只有快速正向层确实存在空组时，才构建快速反向层。若各组均有
                // 候选但组合失败，直接进入原完整正向搜索，避免普通组合失败又
                // 重复支付整轮快速反向池的代价。
                if (!combinationSearchInterrupted &&
                    !searchContext.HasSuccessfulSolution &&
                    fastCandidatePoolsHaveEmptyGroup)
                {
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref searchContext.ProcessedStateCount),
                        Message = "快速正向层存在空候选组（" +
                            FormatEmptyCandidatePoolGroupKeys(
                                routeRequest,
                                fastCandidatePools) +
                            "），正在补充反向快速候选。"
                    });
                    var fastReverseCandidatePools = BuildFastCandidatePools(
                        routeRequest,
                        fixedTerminalAndObstacleSolids,
                        LinkageRouteTraversalDirection.Reverse,
                        mainSearchCancellationToken);
                    fastCandidatePools = MergeTraversalCandidatePools(
                        routeRequest,
                        fastForwardCandidatePools,
                        fastReverseCandidatePools);
                    fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(
                        fastCandidatePools);
                    if (!mainSearchCancellationToken.IsCancellationRequested &&
                        !fastCandidatePoolsHaveEmptyGroup)
                    {
                        searchContext.BeginContinuationStage();
                        combinationSearchInterrupted |= RunCombinationSearch(
                            searchContext,
                            fastCandidatePools,
                            stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: Math.Max(
                                1,
                                Math.Min(
                                    routeRequest.SearchOptions
                                        .FastSearchStageLimitMilliseconds,
                                    routeRequest.SearchOptions
                                        .FirstSolutionTargetMilliseconds)),
                            allowClearanceExpansionCandidates: false,
                            exploreAllPoolOrders: false);
                    }
                }

                if (!combinationSearchInterrupted && !searchContext.HasSuccessfulSolution &&
                    fastCandidatePoolsHaveEmptyGroup && routeRequest.Groups.Any(group =>
                        group.HasSectionSpecificBusbarSettings(routeRequest) || group.EndTerminal?.CabinetBridgeAssembly != null))
                {
                    using (var physicalSeedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        mainSearchCancellationToken))
                    {
                        physicalSeedCancellation.CancelAfter(Math.Max(1, Math.Min(
                            routeRequest.SearchOptions.FastSearchStageLimitMilliseconds,
                            routeRequest.SearchOptions.FirstSolutionTargetMilliseconds)));
                        fastCandidatePools = FillEmptyPoolsWithPhysicalProxySeeds(routeRequest,
                            fastCandidatePools, fixedTerminalAndObstacleSolids, physicalSeedCancellation.Token);
                    }
                    fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(fastCandidatePools);
                    if (!mainSearchCancellationToken.IsCancellationRequested && !fastCandidatePoolsHaveEmptyGroup)
                    {
                        searchContext.BeginContinuationStage();
                        combinationSearchInterrupted |= RunCombinationSearch(searchContext, fastCandidatePools,
                            stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: Math.Max(1, Math.Min(
                                routeRequest.SearchOptions.FastSearchStageLimitMilliseconds,
                                routeRequest.SearchOptions.FirstSolutionTargetMilliseconds)),
                            allowClearanceExpansionCandidates: false, exploreAllPoolOrders: false);
                    }
                }

                if (!combinationSearchInterrupted &&
                    !searchContext.HasSuccessfulSolution &&
                    fastCandidatePoolsHaveEmptyGroup)
                {
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref searchContext.ProcessedStateCount),
                        Message = "快速正反向层仍有空候选组（" +
                            FormatEmptyCandidatePoolGroupKeys(
                                routeRequest,
                                fastCandidatePools) +
                            "），正在流式探测困难组首个绝缘候选。"
                    });
                    using (var fastClearanceSeedCancellationSource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            mainSearchCancellationToken))
                    {
                        fastClearanceSeedCancellationSource.CancelAfter(Math.Max(
                            1,
                            Math.Min(
                                routeRequest.SearchOptions
                                    .FastSearchStageLimitMilliseconds,
                                routeRequest.SearchOptions
                                    .FirstSolutionTargetMilliseconds)));
                        fastCandidatePools = FillEmptyPoolsWithClearanceSeeds(
                            routeRequest,
                            fastCandidatePools,
                            fixedTerminalAndObstacleSolids,
                            fastClearanceSeedCancellationSource.Token);
                    }
                    fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(
                        fastCandidatePools);
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref searchContext.ProcessedStateCount),
                        Message = fastCandidatePoolsHaveEmptyGroup
                            ? "流式困难组探测未找到候选，转入下一层限时短路径。"
                            : "流式困难组探测已补齐候选，正在组合首方案。"
                    });
                    if (!mainSearchCancellationToken.IsCancellationRequested &&
                        !fastCandidatePoolsHaveEmptyGroup)
                    {
                        searchContext.BeginContinuationStage();
                        combinationSearchInterrupted |= RunCombinationSearch(
                            searchContext,
                            fastCandidatePools,
                            stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: Math.Max(
                                1,
                                Math.Min(
                                    routeRequest.SearchOptions
                                        .FastSearchStageLimitMilliseconds,
                                    routeRequest.SearchOptions
                                        .FirstSolutionTargetMilliseconds)),
                            allowClearanceExpansionCandidates: false,
                            exploreAllPoolOrders: false);
                    }
                }

                if (!combinationSearchInterrupted &&
                    !searchContext.HasSuccessfulSolution &&
                    fastCandidatePoolsHaveEmptyGroup)
                {
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref searchContext.ProcessedStateCount),
                        Message = "首层仍有空候选组，正在限时补充下一层短路径候选。"
                    });
                    using (var extendedSeedCancellationSource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            mainSearchCancellationToken))
                    {
                        extendedSeedCancellationSource.CancelAfter(Math.Max(
                            1,
                            Math.Min(
                                routeRequest.SearchOptions
                                    .ExtendedFastSearchStageLimitMilliseconds,
                                routeRequest.SearchOptions
                                    .FirstSolutionTargetMilliseconds)));
                        fastCandidatePools = ExpandPoolsWithClearanceSeeds(
                            routeRequest,
                            fastCandidatePools,
                            fixedTerminalAndObstacleSolids,
                            extendedSeedCancellationSource.Token);
                    }
                    fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(
                        fastCandidatePools);
                    if (fastCandidatePoolsHaveEmptyGroup)
                    {
                        progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = algorithmKind,
                            ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                            ProcessedStateCount = Interlocked.Read(
                                ref searchContext.ProcessedStateCount),
                            Message = "下一层限时短路径仍为空的逻辑组：" +
                                string.Join(",", routeRequest.Groups
                                    .Where((routeGroupRequest, groupIndex) =>
                                        fastCandidatePools?[groupIndex] == null ||
                                        fastCandidatePools[groupIndex]
                                            .ValidCandidates.Count == 0)
                                    .Select(routeGroupRequest =>
                                        routeGroupRequest.GroupKey))
                        });
                    }
                    if (!fastCandidatePoolsHaveEmptyGroup)
                    {
                        searchContext.BeginContinuationStage();
                        combinationSearchInterrupted |= RunCombinationSearch(
                            searchContext,
                            fastCandidatePools,
                            stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: Math.Max(
                                1,
                                Math.Min(
                                    routeRequest.SearchOptions
                                        .ExtendedFastSearchStageLimitMilliseconds,
                                    routeRequest.SearchOptions
                                        .FirstSolutionTargetMilliseconds)),
                            allowClearanceExpansionCandidates: false,
                        exploreAllPoolOrders: false);
                    }
                }

                if (!combinationSearchInterrupted && !searchContext.HasSuccessfulSolution &&
                    fastCandidatePoolsHaveEmptyGroup && routeRequest.Groups.Any(group =>
                        group.HasSectionSpecificBusbarSettings(routeRequest) || group.EndTerminal?.CabinetBridgeAssembly != null))
                {
                    // Give original XYZ/N/M and new critical-plane families
                    // independent finite turns. Per-skeleton interleaving lets
                    // one expensive family starve later original skeletons.
                    foreach (var useCriticalPlaneFamily in new[] { false, true })
                    {
                    if (mainSearchCancellationToken.IsCancellationRequested ||
                        !fastCandidatePoolsHaveEmptyGroup || combinationSearchInterrupted ||
                        searchContext.HasSuccessfulSolution) break;
                    var finiteFamilyName = useCriticalPlaneFamily ? "临界平面" : "原始XYZ/N/M";
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(ref searchContext.ProcessedStateCount),
                        Message = "困难组" + finiteFamilyName + "独立有限层：交付完整有效首种子，保留原完整枚举。"
                    });
                    using (var criticalSeedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        mainSearchCancellationToken))
                    {
                        criticalSeedCancellation.CancelAfter(Math.Max(1,
                            routeRequest.SearchOptions.FirstSolutionTargetMilliseconds));
                        fastCandidatePools = FillEmptyPoolsWithFiniteFamilySeeds(routeRequest,
                            fastCandidatePools, fixedTerminalAndObstacleSolids, criticalSeedCancellation.Token,
                            useCriticalPlaneFamily);
                    }
                    fastCandidatePoolsHaveEmptyGroup = HasEmptyCandidatePool(fastCandidatePools);
                    if (!mainSearchCancellationToken.IsCancellationRequested && !fastCandidatePoolsHaveEmptyGroup)
                    {
                        searchContext.BeginContinuationStage();
                        combinationSearchInterrupted |= RunCombinationSearch(searchContext, fastCandidatePools,
                            stopAfterFirstSuccessfulSolution: true,
                            maximumStageMilliseconds: Math.Max(1, Math.Min(
                                routeRequest.SearchOptions.ExtendedFastSearchStageLimitMilliseconds,
                                routeRequest.SearchOptions.FirstSolutionTargetMilliseconds)),
                            allowClearanceExpansionCandidates: false,
                            allowDeepClearanceExpansionCandidates: false, exploreAllPoolOrders: false);
                    }
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(ref searchContext.ProcessedStateCount),
                        Message = fastCandidatePoolsHaveEmptyGroup
                            ? finiteFamilyName + "种子仍有空组：" + FormatEmptyCandidatePoolGroupKeys(routeRequest, fastCandidatePools)
                            : finiteFamilyName + "种子已交组合搜索，继续原完整枚举。"
                    });
                    }
                }

                // 快速层各补充阶段仍未成套：按顺序避让重规划逐组落位，已落位组实体作为后续组的避让实体。
                if (!combinationSearchInterrupted && !searchContext.HasSuccessfulSolution &&
                    !mainSearchCancellationToken.IsCancellationRequested)
                {
                    // 附加阶段不并入 combinationSearchInterrupted：原搜索后续各层照常运行。
                    var replanningSkipReason = GetPrioritizedObstacleReplanningSkipReason(routeRequest);
                    progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = algorithmKind,
                        ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        Message = replanningSkipReason == null
                            ? "快速层未成套，开始顺序避让重规划。"
                            : "快速层未成套；顺序避让重规划不运行：" + replanningSkipReason + "。"
                    });
                    if (replanningSkipReason == null)
                        RunPrioritizedObstacleReplanning(searchContext, fastCandidatePools,
                            2 * routeRequest.SearchOptions.DeepTraversalTimeSliceMilliseconds);
                }
                if (routeRequest.Groups.Any(group => group.HasSectionSpecificBusbarSettings(routeRequest) ||
                    group.EndTerminal?.CabinetBridgeAssembly != null ||
                    LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(group.StartTerminal, out _)))
                    retainedPhysicalFinitePools = fastCandidatePools;
            }

            // Never delay fallback branches behind the original unbounded
            // complete pool construction. Only the finite seed layer owns
            // this handoff; finally/cancellation in Solve cover early exits.
            finiteSeedStageCompleted?.Invoke();
            if (!mainSearchCancellationToken.IsCancellationRequested &&
                !searchContext.HasSuccessfulSolution &&
                algorithmKind == LinkageRouteAlgorithmKind.SweptRepair &&
                routeRequest.Groups.Any(group => group.HasSectionSpecificBusbarSettings(routeRequest)))
            {
                var physicalLayerPools = new LinkageRouteCandidatePool[routeRequest.Groups.Count];
                Parallel.For(0, routeRequest.Groups.Count, new ParallelOptions {
                    MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
                }, groupIndex =>
                {
                    using (var layerSlice = CancellationTokenSource.CreateLinkedTokenSource(mainSearchCancellationToken))
                    {
                        layerSlice.CancelAfter(routeRequest.SearchOptions.DeepTraversalTimeSliceMilliseconds);
                        physicalLayerPools[groupIndex] = LinkageRouteCandidateFactory.BuildPhysicalLayerSeedPool(
                            routeRequest,routeRequest.Groups[groupIndex],fixedTerminalAndObstacleSolids,layerSlice.Token);
                    }
                });
                var physicalFinitePools = MergeFiniteAndFullCandidatePools(routeRequest,
                    physicalLayerPools,retainedPhysicalFinitePools);
                retainedPhysicalFinitePools = physicalFinitePools;
                if (!mainSearchCancellationToken.IsCancellationRequested && !HasEmptyCandidatePool(physicalFinitePools))
                {
                    progress?.Report(new LinkageRouteProgress {
                        AlgorithmKind=algorithmKind,ElapsedMilliseconds=searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount=Interlocked.Read(ref searchContext.ProcessedStateCount),
                        Message="实际端头分层XYZ候选已完成实体校验，正在限时组合完整方案。"
                    });
                    searchContext.BeginContinuationStage();
                    combinationSearchInterrupted |= RunCombinationSearch(searchContext,physicalFinitePools,
                        stopAfterFirstSuccessfulSolution:true,
                        maximumStageMilliseconds:routeRequest.SearchOptions.ConstraintDirectedDeepRepairLimitMilliseconds,
                        allowClearanceExpansionCandidates:false,allowPairViolationDirectedCandidates:false,
                        allowDeepClearanceExpansionCandidates:false,exploreAllPoolOrders:false);
                }
            }
            if (!mainSearchCancellationToken.IsCancellationRequested && !searchContext.HasSuccessfulSolution &&
                algorithmKind == LinkageRouteAlgorithmKind.SweptRepair && retainedPhysicalFinitePools != null &&
                routeRequest.Groups.Any(group => group.HasSectionSpecificBusbarSettings(routeRequest) ||
                    group.EndTerminal?.CabinetBridgeAssembly != null))
            {
                // One locally valid pose may conflict with another group. Keep
                // it and also retain actual terminal/critical-plane alternatives
                // before the exhaustive pool build can monopolize the search.
                using (var alternativeSlice = CancellationTokenSource.CreateLinkedTokenSource(mainSearchCancellationToken))
                {
                    alternativeSlice.CancelAfter(routeRequest.SearchOptions.FirstSolutionTargetMilliseconds);
                    retainedPhysicalFinitePools = FillEmptyPoolsWithFiniteFamilySeeds(routeRequest,
                        retainedPhysicalFinitePools, fixedTerminalAndObstacleSolids, alternativeSlice.Token, true, true);
                }
                var sectionSiblingPools = new LinkageRouteCandidatePool[routeRequest.Groups.Count];
                Parallel.For(0, routeRequest.Groups.Count, new ParallelOptions {
                    MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
                }, groupIndex =>
                {
                    using (var siblingSlice = CancellationTokenSource.CreateLinkedTokenSource(mainSearchCancellationToken))
                    {
                        siblingSlice.CancelAfter(routeRequest.SearchOptions.DeepTraversalTimeSliceMilliseconds);
                        sectionSiblingPools[groupIndex] = LinkageRouteCandidateFactory.BuildSectionPlacementSiblingSeedPool(
                            routeRequest, routeRequest.Groups[groupIndex], retainedPhysicalFinitePools[groupIndex],
                            fixedTerminalAndObstacleSolids, siblingSlice.Token);
                    }
                });
                retainedPhysicalFinitePools = MergeFiniteAndFullCandidatePools(routeRequest,
                    retainedPhysicalFinitePools, sectionSiblingPools, preserveCompletedCandidates: true);
                if (!mainSearchCancellationToken.IsCancellationRequested && !HasEmptyCandidatePool(retainedPhysicalFinitePools))
                {
                    progress?.Report(new LinkageRouteProgress {
                        AlgorithmKind = algorithmKind, ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(ref searchContext.ProcessedStateCount),
                        Message = "分段实际搭接层位候选已保留，正在组合固定端其他允许落位及临界绕行方案。"
                    });
                    searchContext.BeginContinuationStage();
                    combinationSearchInterrupted |= RunCombinationSearch(searchContext, retainedPhysicalFinitePools,
                        stopAfterFirstSuccessfulSolution:true,
                        maximumStageMilliseconds:routeRequest.SearchOptions.ConstraintDirectedDeepRepairLimitMilliseconds,
                        allowClearanceExpansionCandidates:false,allowPairViolationDirectedCandidates:false,
                        allowDeepClearanceExpansionCandidates:false,exploreAllPoolOrders:false);
                }
            }
            if (!mainSearchCancellationToken.IsCancellationRequested &&
                !searchContext.HasSuccessfulSolution &&
                algorithmKind == LinkageRouteAlgorithmKind.SweptRepair &&
                retainedPhysicalFinitePools != null && !HasEmptyCandidatePool(retainedPhysicalFinitePools) &&
                routeRequest.Groups.Any(group => group.EndTerminal?.CabinetBridgeAssembly != null))
            {
                // 多列柜顶的完整有限种子已齐时，先在既有修复预算内处理真实相间冲突。
                // 不能让完整基础组合穷举挡住位移修复；候选及门禁均保持，届满继续原枚举。
                progress?.Report(new LinkageRouteProgress {
                    AlgorithmKind = algorithmKind, ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                    ProcessedStateCount = Interlocked.Read(ref searchContext.ProcessedStateCount),
                    Message = "多列柜顶有限种子已齐，按实际冲突执行有界位移修复。"
                });
                searchContext.BeginContinuationStage();
                combinationSearchInterrupted |= RunCombinationSearch(searchContext, retainedPhysicalFinitePools,
                    stopAfterFirstSuccessfulSolution: true,
                    maximumStageMilliseconds: routeRequest.SearchOptions.ConstraintDirectedDeepRepairLimitMilliseconds,
                    allowClearanceExpansionCandidates: true, allowPairViolationDirectedCandidates: true,
                    allowDeepClearanceExpansionCandidates: false, exploreAllPoolOrders: false);
            }
            var useCandidateContinuation = retainedPhysicalFinitePools != null &&
                algorithmKind == LinkageRouteAlgorithmKind.SweptRepair;
            var forwardCandidatePools = useCandidateContinuation
                ? BuildCandidatePoolsContinuously(searchContext, retainedPhysicalFinitePools,
                    fixedTerminalAndObstacleSolids)
                : BuildCandidatePools(routeRequest, algorithmKind, fixedTerminalAndObstacleSolids,
                    LinkageRouteTraversalDirection.Forward, mainSearchCancellationToken);
            forwardCandidatePools = MergeFiniteAndFullCandidatePools(routeRequest,
                forwardCandidatePools, retainedPhysicalFinitePools,
                preserveCompletedCandidates: useCandidateContinuation);
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools =
                forwardCandidatePools;
            combinationSearchInterrupted |=
                mainSearchCancellationToken.IsCancellationRequested;
            var forwardPoolHasEmptyGroup = forwardCandidatePools.Any(
                candidatePool => candidatePool == null ||
                    candidatePool.ValidCandidates.Count == 0);
            if (!combinationSearchInterrupted &&
                !forwardPoolHasEmptyGroup)
            {
                // 正向批次先搜索每组物理始端→柜顶方向最优的候选层。该层无完整
                // 方案时才开放背向或绕行分支；反向候选不删除，仍参加后续全局排序。
                combinationSearchInterrupted = RunPhysicalForwardFirstSearch(
                    searchContext,
                    forwardCandidatePools);
            }

            if (!mainSearchCancellationToken.IsCancellationRequested)
            {
                progress?.Report(new LinkageRouteProgress
                {
                    AlgorithmKind = algorithmKind,
                    ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                    ProcessedStateCount = Interlocked.Read(
                        ref searchContext.ProcessedStateCount),
                    Message = "正向首发阶段结束，正在构建反向遍历候选并继续全局排序。"
                });
                var reverseCandidatePools = BuildCandidatePools(
                    routeRequest,
                    algorithmKind,
                    fixedTerminalAndObstacleSolids,
                    LinkageRouteTraversalDirection.Reverse,
                    mainSearchCancellationToken);
                if (!mainSearchCancellationToken.IsCancellationRequested)
                {
                    candidatePools = MergeTraversalCandidatePools(
                        routeRequest,
                        forwardCandidatePools,
                        reverseCandidatePools,
                        preserveCompletedCandidates: useCandidateContinuation);
                    if (algorithmKind == LinkageRouteAlgorithmKind.SweptRepair &&
                        !searchContext.HasSuccessfulSolution && HasEmptyCandidatePool(candidatePools))
                    {
                        progress?.Report(new LinkageRouteProgress {
                            AlgorithmKind = algorithmKind,
                            ElapsedMilliseconds = searchStopwatch.ElapsedMilliseconds,
                            ProcessedStateCount = Interlocked.Read(ref searchContext.ProcessedStateCount),
                            Message = "保留已完成相组，仅为空组补充有界临界绕行候选。"
                        });
                        var repairedEmptyPools = candidatePools.ToArray();
                        using (var emptyPoolSlice = CancellationTokenSource.CreateLinkedTokenSource(
                            mainSearchCancellationToken))
                        {
                            emptyPoolSlice.CancelAfter(routeRequest.SearchOptions.ConstraintDirectedDeepRepairLimitMilliseconds);
                            for (var groupIndex = 0; groupIndex < repairedEmptyPools.Length; groupIndex++)
                            {
                                if (emptyPoolSlice.IsCancellationRequested) break;
                                if (repairedEmptyPools[groupIndex]?.ValidCandidates.Count > 0) continue;
                                var criticalRepair = LinkageRouteCandidateFactory.BuildEmptyPoolCriticalRepair(
                                    routeRequest, routeRequest.Groups[groupIndex], fixedTerminalAndObstacleSolids,
                                    emptyPoolSlice.Token);
                                repairedEmptyPools[groupIndex] = LinkageRouteCandidateFactory.MergeTraversalPools(
                                    routeRequest, new[] { repairedEmptyPools[groupIndex], criticalRepair },
                                    preserveCompletedCandidates: true);
                            }
                        }
                        candidatePools = repairedEmptyPools;
                    }
                    var emptyCandidatePool = candidatePools.FirstOrDefault(
                        candidatePool => candidatePool == null ||
                            candidatePool.ValidCandidates.Count == 0);
                    if (emptyCandidatePool != null)
                    {
                        searchContext.CompletePendingSolutionOptimizations();
                        return new[]
                        {
                            CreatePoolFailure(
                                algorithmKind,
                                candidatePools,
                                emptyCandidatePool,
                                searchStopwatch.ElapsedMilliseconds)
                        };
                    }

                    combinationSearchInterrupted |= RunStagedCombinationSearch(
                        searchContext,
                        candidatePools);
                }
            }
            combinationSearchInterrupted |=
                mainSearchCancellationToken.IsCancellationRequested;

            // 全部搜索阶段正常走完（未被时间片、时限或取消中断）= 路径已找完：
            // 全部线程转去优化已发布方案，再等待后台优化收尾。
            if (!combinationSearchInterrupted)
                searchContext.ActivateExhaustedMainSearchOptimization();
            searchContext.CompletePendingSolutionOptimizations();
            combinationSearchInterrupted |=
                mainSearchCancellationToken.IsCancellationRequested;
            var rankedSolutions = searchContext.GetRankedSolutions();
            var bestFailureSolution = searchContext.GetBestFailureSolution();
            if (combinationSearchInterrupted && bestFailureSolution != null)
            {
                bestFailureSolution.IsCancelled = true;
                bestFailureSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
                {
                    Stage = LinkageRouteFailureStage.Cancelled,
                    Message = "组合搜索在时限或用户取消前结束；已保留此前找到的最优成功和失败方案。",
                    CompletedGroupCount = bestFailureSolution.Diagnostics
                        .Select(diagnostic => diagnostic.CompletedGroupCount)
                        .DefaultIfEmpty(0)
                        .Max()
                });
            }

            if (bestFailureSolution != null && rankedSolutions.Count == 0)
            {
                rankedSolutions.Add(bestFailureSolution);
            }
            else if (rankedSolutions.Count == 0)
            {
                var noSolutionFailure = CreateCombinationFailure(
                    algorithmKind,
                    new List<LinkageRouteCandidate>(),
                    null,
                    0,
                    1,
                    0d,
                    combinationSearchInterrupted
                        ? "组合搜索在时限或用户取消前尚未找到完整成功方案。"
                        : "所有生成顺序和完整组合均未通过相间三维绝缘校验。");
                if (combinationSearchInterrupted)
                {
                    noSolutionFailure.IsCancelled = true;
                    noSolutionFailure.Diagnostics[0].Stage =
                        LinkageRouteFailureStage.Cancelled;
                }

                rankedSolutions.Add(noSolutionFailure);
            }

            return rankedSolutions;
        }

        private static bool RunStagedCombinationSearch(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools)
        {
            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "基础层：搜索正反向基础候选和低成本组合。"
            });
            searchContext.BeginContinuationStage();
            var stagedSearchInterrupted = RunCombinationSearch(
                searchContext,
                candidatePools,
                stopAfterFirstSuccessfulSolution: false,
                allowClearanceExpansionCandidates: false,
                allowPairViolationDirectedCandidates: false,
                allowDeepClearanceExpansionCandidates: false,
                exploreAllPoolOrders: false);
            if (stagedSearchInterrupted ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested)
            {
                return stagedSearchInterrupted ||
                    searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }

            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "基础层穷尽后进入浅层：开放2～4段绝缘临界动态绕障候选。"
            });
            searchContext.BeginContinuationStage();
            stagedSearchInterrupted |= RunCombinationSearch(
                searchContext,
                candidatePools,
                stopAfterFirstSuccessfulSolution: false,
                allowClearanceExpansionCandidates: true,
                allowPairViolationDirectedCandidates: false,
                allowDeepClearanceExpansionCandidates: false,
                exploreAllPoolOrders: false);
            if (stagedSearchInterrupted ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested)
            {
                return stagedSearchInterrupted ||
                    searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }

            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "浅层穷尽后进入定向修复层：按真实冲突方向和侵入深度执行极限位移修复。"
            });
            searchContext.BeginContinuationStage();
            stagedSearchInterrupted |= RunCombinationSearch(
                searchContext,
                candidatePools,
                stopAfterFirstSuccessfulSolution: false,
                allowClearanceExpansionCandidates: true,
                allowPairViolationDirectedCandidates: true,
                allowDeepClearanceExpansionCandidates: false,
                exploreAllPoolOrders: false);
            if (stagedSearchInterrupted ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested)
            {
                return stagedSearchInterrupted ||
                    searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }

            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "定向修复穷尽后进入深层：开放全部生成顺序、2～8段边界绕行和深层候选；" +
                    "定向生成调用={0}，有效候选={1}，首个失败={2}。",
                    Interlocked.Read(
                        ref searchContext.PairViolationRepairGenerationCount),
                    Interlocked.Read(
                        ref searchContext.PairViolationRepairCandidateCount),
                    searchContext.PairViolationFirstFailureDiagnostic ?? "<无>" )
            });
            searchContext.BeginContinuationStage();
            stagedSearchInterrupted |= RunConstraintDirectedDeepRepairSearch(
                searchContext,
                candidatePools);
            if (stagedSearchInterrupted ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested)
            {
                return stagedSearchInterrupted ||
                    searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }

            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "最受约束深层修复未形成完整方案，继续开放全部根候选和组顺序。"
            });
            searchContext.BeginContinuationStage();
            stagedSearchInterrupted |= RunCombinationSearch(
                searchContext,
                candidatePools,
                stopAfterFirstSuccessfulSolution: false,
                allowClearanceExpansionCandidates: true,
                allowPairViolationDirectedCandidates: true,
                allowDeepClearanceExpansionCandidates: true,
                exploreAllPoolOrders: true);
            return stagedSearchInterrupted ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested;
        }

        private static bool RunConstraintDirectedDeepRepairSearch(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools)
        {
            var zeroCompatibilityPair = FindFirstZeroCompatibilityPoolPair(
                searchContext,
                candidatePools);
            if (zeroCompatibilityPair == null ||
                searchContext.MainSearchCancellationToken.IsCancellationRequested)
            {
                return searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }

            // 先固定基础候选较多的一侧，把候选较少、约束更强的一侧作为首个
            // 动态修复目标。这样能够尽快形成一个小规模可行种子；后续递归仍按
            // 兼容候选数选择下一困难组，并保留完整深层搜索作为兜底。
            var firstPool = zeroCompatibilityPair.Item1;
            var secondPool = zeroCompatibilityPair.Item2;
            var rootCandidatePool = firstPool.ValidCandidates.Count >=
                    secondPool.ValidCandidates.Count
                ? firstPool
                : secondPool;
            var repairCandidatePool = ReferenceEquals(rootCandidatePool, firstPool)
                ? secondPool
                : firstPool;
            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "深层最受约束修复：基础候选零兼容边=" +
                    rootCandidatePool.GroupRequest.GroupKey + "→" +
                    repairCandidatePool.GroupRequest.GroupKey +
                    "，先固定少量根候选并连续修复后续零兼容组。"
            });

            using (var searchStage = new CombinationSearchStage(
                stopAfterFirstSuccessfulSolution: true,
                searchContext.MainSearchCancellationToken,
                searchContext.RouteRequest.SearchOptions
                    .ConstraintDirectedDeepRepairLimitMilliseconds,
                allowClearanceExpansionCandidates: true,
                allowPairViolationDirectedCandidates: true,
                allowDeepClearanceExpansionCandidates: true,
                exploreAllPoolOrders: false))
            {
                var rootCandidates = OrderCandidates(
                        rootCandidatePool.ValidCandidates,
                        searchContext.AlgorithmKind,
                        searchContext.RouteRequest.SearchOptions.BeamWidth)
                    .Take(searchContext.RouteRequest.SearchOptions
                        .ConstraintDirectedRootCandidateCount)
                    .ToList();
                foreach (var rootCandidate in rootCandidates)
                {
                    if (searchStage.ShouldStop)
                    {
                        break;
                    }

                    Interlocked.Increment(ref searchContext.ProcessedStateCount);
                    var selectedCandidates = new List<LinkageRouteCandidate>
                    {
                        rootCandidate
                    };
                    var remainingCandidatePools = candidatePools
                        .Where(candidatePool => !ReferenceEquals(
                            candidatePool,
                            rootCandidatePool))
                        .ToList();
                    SearchCombinations(
                        searchContext,
                        remainingCandidatePools,
                        selectedCandidates,
                        searchStage);
                }
            }

            return searchContext.MainSearchCancellationToken.IsCancellationRequested;
        }

        private static Tuple<LinkageRouteCandidatePool, LinkageRouteCandidatePool>
            FindFirstZeroCompatibilityPoolPair(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools)
        {
            var orderedPoolPairs = new List<Tuple<
                LinkageRouteCandidatePool,
                LinkageRouteCandidatePool>>();
            for (var firstPoolIndex = 0;
                firstPoolIndex < candidatePools.Count;
                firstPoolIndex++)
            {
                for (var secondPoolIndex = firstPoolIndex + 1;
                    secondPoolIndex < candidatePools.Count;
                    secondPoolIndex++)
                {
                    orderedPoolPairs.Add(Tuple.Create(
                        candidatePools[firstPoolIndex],
                        candidatePools[secondPoolIndex]));
                }
            }

            foreach (var poolPair in orderedPoolPairs
                .Where(candidatePoolPair =>
                    candidatePoolPair.Item1?.ValidCandidates.Count > 0 &&
                    candidatePoolPair.Item2?.ValidCandidates.Count > 0)
                .OrderBy(candidatePoolPair =>
                    (long)candidatePoolPair.Item1.ValidCandidates.Count *
                    candidatePoolPair.Item2.ValidCandidates.Count)
                .ThenBy(candidatePoolPair =>
                    candidatePoolPair.Item1.GroupRequest.GroupKey,
                    StringComparer.Ordinal)
                .ThenBy(candidatePoolPair =>
                    candidatePoolPair.Item2.GroupRequest.GroupKey,
                    StringComparer.Ordinal))
            {
                var hasCompatiblePair = false;
                foreach (var firstCandidate in searchContext.GetOrderedCandidates(
                    poolPair.Item1))
                {
                    foreach (var secondCandidate in searchContext.GetOrderedCandidates(
                        poolPair.Item2))
                    {
                        if (searchContext.MainSearchCancellationToken.IsCancellationRequested)
                        {
                            return null;
                        }

                        var pairEvaluation = searchContext.EvaluateAgainstSelected(
                            secondCandidate,
                            new[] { firstCandidate });
                        if (!pairEvaluation.IsClear)
                        {
                            continue;
                        }

                        hasCompatiblePair = true;
                        break;
                    }

                    if (hasCompatiblePair)
                    {
                        break;
                    }
                }

                if (!hasCompatiblePair)
                {
                    return poolPair;
                }
            }

            return null;
        }

        private static bool RunPhysicalForwardFirstSearch(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> forwardCandidatePools)
        {
            var physicalForwardPreferredPools =
                SelectPhysicalForwardPreferredCandidatePools(forwardCandidatePools);
            var physicalForwardSearchInterrupted = RunCombinationSearch(
                searchContext,
                physicalForwardPreferredPools,
                stopAfterFirstSuccessfulSolution: true,
                maximumStageMilliseconds: Math.Max(
                    1,
                    searchContext.RouteRequest.SearchOptions
                        .FirstSolutionTargetMilliseconds),
                allowClearanceExpansionCandidates: false,
                exploreAllPoolOrders: false);
            if (physicalForwardSearchInterrupted ||
                searchContext.HasSuccessfulSolution)
            {
                return physicalForwardSearchInterrupted;
            }

            // 首末坐标正方向层确实无法组成完整方案时，重新开放同一正向遍历批次
            // 内的背向绕行候选。只清理组合状态，不丢弃距离缓存或失败诊断。
            searchContext.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                ElapsedMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(
                    ref searchContext.ProcessedStateCount),
                Message = "物理正方向首发时间片结束，正在开放全部正向遍历候选。"
            });
            searchContext.BeginContinuationStage();
            return RunCombinationSearch(
                searchContext,
                forwardCandidatePools,
                stopAfterFirstSuccessfulSolution: true,
                maximumStageMilliseconds: Math.Max(
                    1,
                    searchContext.RouteRequest.SearchOptions
                        .FirstSolutionTargetMilliseconds),
                allowClearanceExpansionCandidates: false,
                exploreAllPoolOrders: false);
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            SelectPhysicalForwardPreferredCandidatePools(
            IEnumerable<LinkageRouteCandidatePool> sourceCandidatePools)
        {
            var preferredCandidatePools = new List<LinkageRouteCandidatePool>();
            foreach (var sourceCandidatePool in sourceCandidatePools ??
                Enumerable.Empty<LinkageRouteCandidatePool>())
            {
                var preferredCandidatePool = new LinkageRouteCandidatePool
                {
                    GroupRequest = sourceCandidatePool?.GroupRequest
                };
                if (sourceCandidatePool == null)
                {
                    preferredCandidatePools.Add(preferredCandidatePool);
                    continue;
                }

                foreach (var traversalDirection in
                    sourceCandidatePool.TraversalDirections)
                {
                    preferredCandidatePool.TraversalDirections.Add(
                        traversalDirection);
                }

                var availableCandidates = sourceCandidatePool.ValidCandidates
                    .Where(routeCandidate => routeCandidate != null)
                    .ToList();
                if (availableCandidates.Count == 0)
                {
                    preferredCandidatePools.Add(preferredCandidatePool);
                    continue;
                }

                var minimumOpposingTerminalSegmentCount = availableCandidates
                    .Min(routeCandidate =>
                        routeCandidate.PhysicalOpposingTerminalSegmentCount);
                var bestTerminalDirectionCandidates = availableCandidates
                    .Where(routeCandidate =>
                        routeCandidate.PhysicalOpposingTerminalSegmentCount ==
                            minimumOpposingTerminalSegmentCount)
                    .ToList();
                var minimumPhysicalReverseTravelLength =
                    bestTerminalDirectionCandidates.Min(routeCandidate =>
                        routeCandidate.PhysicalReverseTravelLength);
                foreach (var preferredRouteCandidate in
                    bestTerminalDirectionCandidates.Where(routeCandidate =>
                        Math.Abs(routeCandidate.PhysicalReverseTravelLength -
                            minimumPhysicalReverseTravelLength) <= 1e-6d))
                {
                    preferredCandidatePool.ValidCandidates.Add(
                        preferredRouteCandidate);
                }
                // 垂直双始端的 L 形路径是普通实体路径，不是水平连接排
                // 附件；即使端部中心性不是最小，也必须保留，否则唯一可制造
                // 的 L 拓扑会在首发偏好筛选中被丢掉。
                foreach (var lPathCandidate in availableCandidates.Where(
                    routeCandidate =>
                        (routeCandidate.TopologySignature ?? string.Empty)
                            .IndexOf("DOUBLE-START-L-BRIDGE",
                                StringComparison.Ordinal) >= 0 &&
                        !preferredCandidatePool.ValidCandidates.Contains(
                            routeCandidate)))
                {
                    preferredCandidatePool.ValidCandidates.Add(lPathCandidate);
                }

                preferredCandidatePools.Add(preferredCandidatePool);
            }

            return preferredCandidatePools;
        }

        private static bool RunCombinationSearch(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools,
            bool stopAfterFirstSuccessfulSolution,
            int maximumStageMilliseconds = Timeout.Infinite,
            bool allowClearanceExpansionCandidates = true,
            bool allowPairViolationDirectedCandidates = false,
            bool allowDeepClearanceExpansionCandidates = true,
            bool exploreAllPoolOrders = true,
            bool prioritizeSmallerPools = false)
        {
            searchContext.SetSolutionOptimizationCandidatePools(candidatePools);
            var searchStage = new CombinationSearchStage(
                stopAfterFirstSuccessfulSolution,
                searchContext.MainSearchCancellationToken,
                maximumStageMilliseconds,
                allowClearanceExpansionCandidates,
                allowPairViolationDirectedCandidates,
                allowDeepClearanceExpansionCandidates,
                exploreAllPoolOrders);
            var orderedRootCandidatePools = OrderCandidatePoolsByStartCentrality(
                    candidatePools)
                .ToList();
            if (prioritizeSmallerPools)
                orderedRootCandidatePools = orderedRootCandidatePools.OrderBy(pool => pool.ValidCandidates.Count).ToList();
            if (!exploreAllPoolOrders && orderedRootCandidatePools.Count > 1)
            {
                orderedRootCandidatePools = orderedRootCandidatePools.Take(1).ToList();
            }

            var rootBranches = orderedRootCandidatePools
                .SelectMany(candidatePool => OrderCandidates(
                        candidatePool.ValidCandidates,
                        searchContext.AlgorithmKind,
                        searchContext.RouteRequest.SearchOptions.BeamWidth)
                    .Select(routeCandidate => new RootSearchBranch
                    {
                        CandidatePool = candidatePool,
                        RouteCandidate = routeCandidate
                    }))
                .ToList();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(
                    searchContext.RouteRequest)
            };
            try
            {
                Parallel.ForEach(rootBranches, parallelOptions,
                    (rootBranch, rootLoopState) =>
                    {
                        if (searchStage.ShouldStop)
                        {
                            rootLoopState.Stop();
                            return;
                        }

                        if (!searchContext.TryEnterMainSearchWorker(
                                searchStage.CancellationToken))
                        {
                            rootLoopState.Stop();
                            return;
                        }

                        try
                        {
                            Interlocked.Increment(
                                ref searchContext.ProcessedStateCount);
                            var selectedCandidates =
                                new List<LinkageRouteCandidate>
                                {
                                    rootBranch.RouteCandidate
                                };
                            var remainingCandidatePools = candidatePools
                                .Where(candidatePool => !ReferenceEquals(
                                    candidatePool,
                                    rootBranch.CandidatePool))
                                .ToList();
                            SearchCombinations(
                                searchContext,
                                remainingCandidatePools,
                                selectedCandidates,
                                searchStage);
                            if (searchStage.ShouldStop)
                            {
                                rootLoopState.Stop();
                            }
                        }
                        finally
                        {
                            searchContext.ExitMainSearchWorker();
                        }
                    });
            }
            catch (OperationCanceledException) when (
                searchStage.CancellationToken.IsCancellationRequested)
            {
                return searchContext.MainSearchCancellationToken.IsCancellationRequested;
            }
            finally
            {
                searchStage.Dispose();
            }

            return searchContext.MainSearchCancellationToken.IsCancellationRequested;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            OrderCandidatePoolsByStartCentrality(
                IEnumerable<LinkageRouteCandidatePool> candidatePools)
        {
            var candidatePoolList = candidatePools
                .Where(candidatePool => candidatePool?.GroupRequest?.StartTerminal?.RouteAnchor != null)
                .ToList();
            if (candidatePoolList.Count <= 1)
            {
                return candidatePoolList;
            }

            var startAnchorCenter = new Point3d(
                candidatePoolList.Average(candidatePool =>
                    candidatePool.GroupRequest.StartTerminal.RouteAnchor.X),
                candidatePoolList.Average(candidatePool =>
                    candidatePool.GroupRequest.StartTerminal.RouteAnchor.Y),
                candidatePoolList.Average(candidatePool =>
                    candidatePool.GroupRequest.StartTerminal.RouteAnchor.Z));
            return candidatePoolList
                .OrderBy(candidatePool => candidatePool.GroupRequest
                    .StartTerminal.RouteAnchor.DistanceTo(startAnchorCenter))
                .ThenBy(candidatePool => candidatePool.GroupRequest.GroupKey,
                    StringComparer.Ordinal)
                .ToList();
        }

        private static bool ShouldUseFastSeedStage(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return routeRequest?.Groups != null &&
                routeRequest.Groups.Count > 0 &&
                routeRequest.SearchOptions?.PrioritizeAllSolutions != true;
        }

        private static bool HasEmptyCandidatePool(
            IEnumerable<LinkageRouteCandidatePool> candidatePools)
        {
            return (candidatePools ?? Enumerable.Empty<LinkageRouteCandidatePool>())
                .Any(candidatePool => candidatePool == null ||
                    candidatePool.ValidCandidates.Count == 0);
        }

        private static string FormatEmptyCandidatePoolGroupKeys(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCandidatePool> candidatePools)
        {
            if (routeRequest?.Groups == null)
            {
                return "<未知>";
            }

            var emptyCandidatePoolGroupKeys = routeRequest.Groups
                .Where((routeGroupRequest, routeGroupIndex) =>
                    candidatePools == null ||
                    routeGroupIndex >= candidatePools.Count ||
                    candidatePools[routeGroupIndex] == null ||
                    candidatePools[routeGroupIndex].ValidCandidates.Count == 0)
                .Select(routeGroupRequest => routeGroupRequest.GroupKey)
                .ToList();
            return emptyCandidatePoolGroupKeys.Count == 0
                ? "<无>"
                : string.Join(",", emptyCandidatePoolGroupKeys);
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            BuildFastCandidatePools(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var candidatePools = new LinkageRouteCandidatePool[routeRequest.Groups.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
            };

            using (var fastPoolCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
            {
            fastPoolCancellationSource.CancelAfter(Math.Max(1, Math.Min(
                routeRequest.SearchOptions.FastSearchStageLimitMilliseconds,
                routeRequest.SearchOptions.FirstSolutionTargetMilliseconds)));
            var fastPoolCancellationToken = fastPoolCancellationSource.Token;
            var prefetchWindow = ResolveGroupPrefetchWindow(
                routeRequest, routeRequest.Groups.Count);
            Parallel.For(0, routeRequest.Groups.Count, parallelOptions,
                (groupIndex, candidatePoolLoopState) =>
                {
                    if (fastPoolCancellationToken.IsCancellationRequested)
                    {
                        candidatePoolLoopState.Stop();
                        return;
                    }

                    candidatePools[groupIndex] = LinkageRouteCandidateFactory.BuildFastPool(
                        routeRequest,
                        routeRequest.Groups[groupIndex],
                        fixedTerminalAndObstacleSolids,
                        traversalDirection,
                        fastPoolCancellationToken,
                        prefetchWindow: prefetchWindow);
                });

            }
            return candidatePools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool> FillEmptyPoolsWithFiniteFamilySeeds(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCandidatePool> sourcePools,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            CancellationToken cancellationToken,
            bool useCriticalPlaneFamily, bool includePopulatedPools = false)
        {
            var completedPools = sourcePools.ToArray();
            // 通常只剩一两个困难组为空：组间并行只能用到少数核，空出的名额
            // 按组均分给组内有序预取；池内容与顺序不变。
            var activeGroupCount = Enumerable.Range(0, routeRequest.Groups.Count).Count(groupIndex =>
                (includePopulatedPools || !(sourcePools[groupIndex]?.ValidCandidates.Count > 0)) &&
                (routeRequest.Groups[groupIndex].HasSectionSpecificBusbarSettings(routeRequest) ||
                 routeRequest.Groups[groupIndex].EndTerminal?.CabinetBridgeAssembly != null));
            var prefetchWindow = ResolveGroupPrefetchWindow(routeRequest, activeGroupCount);
            Parallel.For(0, routeRequest.Groups.Count, new ParallelOptions
                { MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest) }, groupIndex =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    (!includePopulatedPools && sourcePools[groupIndex]?.ValidCandidates.Count > 0) ||
                    !(routeRequest.Groups[groupIndex].HasSectionSpecificBusbarSettings(routeRequest) ||
                      routeRequest.Groups[groupIndex].EndTerminal?.CabinetBridgeAssembly != null)) return;
                var finiteSeedPool = useCriticalPlaneFamily
                    ? LinkageRouteCandidateFactory.BuildFirstCriticalPlaneSeedPool(
                        routeRequest, routeRequest.Groups[groupIndex], staticSolids,
                        LinkageRouteTraversalDirection.Forward, cancellationToken, prefetchWindow)
                    : LinkageRouteCandidateFactory.BuildFirstOriginalSeedPool(
                        routeRequest, routeRequest.Groups[groupIndex], staticSolids,
                        LinkageRouteTraversalDirection.Forward, cancellationToken, prefetchWindow);
                completedPools[groupIndex] = LinkageRouteCandidateFactory.MergeTraversalPools(
                    routeRequest, new[] { sourcePools[groupIndex], finiteSeedPool },
                    preserveCompletedCandidates: includePopulatedPools);
            });
            return completedPools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool> FillEmptyPoolsWithPhysicalProxySeeds(
            LinkageMultiGroupRouteRequest physicalRequest,
            IReadOnlyList<LinkageRouteCandidatePool> sourcePools,
            IReadOnlyList<LinkageRouteSolid> physicalStaticSolids,
            CancellationToken cancellationToken)
        {
            // Request transport/search settings remain frozen. Only internal
            // topology views change; every accepted seed is a new actual candidate.
            var topologyRequests = new List<LinkageMultiGroupRouteRequest>();
            foreach (var useEnvelopeWidth in new[] { false, true })
            {
                var topologyRequest = LinkageRouteWorkerContractMapper.FromWorkerRequest(
                    LinkageRouteWorkerContractMapper.ToWorkerRequest(physicalRequest));
                topologyRequest.RuntimeControl = physicalRequest.RuntimeControl;
                topologyRequest.UseLegacyDoubleStartHorizontalTopologyProposal =
                    physicalRequest.UseLegacyDoubleStartHorizontalTopologyProposal;
                topologyRequest.Groups = physicalRequest.Groups.Select(physicalGroup =>
                {
                    var topologyGroup = physicalGroup.CreateGenericPathSearchSpecificationView(physicalRequest);
                    if (useEnvelopeWidth) topologyGroup.BusbarWidth = Math.Max(
                        physicalGroup.GetBusbarWidth(physicalRequest), Math.Max(
                            physicalGroup.GetStartLapBusbarWidth(physicalRequest),
                            physicalGroup.GetCabinetTopLapBusbarWidth(physicalRequest)));
                    return topologyGroup;
                }).ToList();
                if (!useEnvelopeWidth || topologyRequest.Groups.Where((group, index) =>
                    Math.Abs(group.GetBusbarWidth(topologyRequest) -
                        topologyRequests[0].Groups[index].GetBusbarWidth(topologyRequests[0])) > 1e-8d).Any())
                    topologyRequests.Add(topologyRequest);
            }
            var resultPools = sourcePools.ToArray();
            // 本阶段按组串行，同一时刻只构建一个组：全部名额交给组内有序预取。
            var prefetchWindow = ResolveGroupPrefetchWindow(physicalRequest, 1);
            for (var groupIndex = 0; groupIndex < physicalRequest.Groups.Count; groupIndex++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var physicalGroup = physicalRequest.Groups[groupIndex];
                if (sourcePools[groupIndex]?.ValidCandidates.Count > 0 ||
                    !physicalGroup.HasSectionSpecificBusbarSettings(physicalRequest)) continue;
                var poolsToMerge = new List<LinkageRouteCandidatePool> { sourcePools[groupIndex] };
                foreach (var topologyRequest in topologyRequests)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    poolsToMerge.Add(LinkageRouteCandidateFactory.BuildPhysicalProxySeedPool(
                        physicalRequest, physicalGroup, topologyRequest, topologyRequest.Groups[groupIndex],
                        physicalStaticSolids, cancellationToken, prefetchWindow));
                }
                resultPools[groupIndex] = LinkageRouteCandidateFactory.MergeTraversalPools(physicalRequest, poolsToMerge);
            }
            return resultPools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            FillEmptyPoolsWithClearanceSeeds(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCandidatePool> sourceCandidatePools,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            var completedCandidatePools = new LinkageRouteCandidatePool[
                routeRequest.Groups.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
            };
            Parallel.For(0, routeRequest.Groups.Count, parallelOptions,
                (groupIndex, candidatePoolLoopState) =>
                {
                    var sourceCandidatePool = sourceCandidatePools?[groupIndex];
                    if (sourceCandidatePool?.ValidCandidates.Count > 0 ||
                        cancellationToken.IsCancellationRequested)
                    {
                        completedCandidatePools[groupIndex] = sourceCandidatePool;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            candidatePoolLoopState.Stop();
                        }
                        return;
                    }

                    var routeGroupRequest = routeRequest.Groups[groupIndex];
                    var violationDirectedSeedPool =
                        LinkageRouteCandidateFactory.BuildViolationDirectedSeedPool(
                            routeRequest,
                            routeGroupRequest,
                            fixedTerminalAndObstacleSolids,
                            sourceCandidatePool,
                            cancellationToken);
                    LinkageRouteCandidatePool forwardClearanceSeedPool = null;
                    if (!cancellationToken.IsCancellationRequested &&
                        violationDirectedSeedPool.ValidCandidates.Count == 0)
                    {
                        forwardClearanceSeedPool =
                            LinkageRouteCandidateFactory.BuildClearanceSeedPool(
                                routeRequest,
                                routeGroupRequest,
                                fixedTerminalAndObstacleSolids,
                                LinkageRouteTraversalDirection.Forward,
                                cancellationToken);
                    }

                    LinkageRouteCandidatePool reverseClearanceSeedPool = null;
                    if (!cancellationToken.IsCancellationRequested &&
                        violationDirectedSeedPool.ValidCandidates.Count == 0 &&
                        (forwardClearanceSeedPool == null ||
                         forwardClearanceSeedPool.ValidCandidates.Count == 0))
                    {
                        reverseClearanceSeedPool =
                            LinkageRouteCandidateFactory.BuildClearanceSeedPool(
                                routeRequest,
                                routeGroupRequest,
                                fixedTerminalAndObstacleSolids,
                                LinkageRouteTraversalDirection.Reverse,
                                cancellationToken);
                    }

                    completedCandidatePools[groupIndex] =
                        LinkageRouteCandidateFactory.MergeTraversalPools(
                            routeRequest,
                            new[]
                            {
                                sourceCandidatePool,
                                violationDirectedSeedPool,
                                forwardClearanceSeedPool,
                                reverseClearanceSeedPool
                            });
                });
            return completedCandidatePools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            ExpandPoolsWithClearanceSeeds(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCandidatePool> sourceCandidatePools,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            var expandedCandidatePools = new LinkageRouteCandidatePool[
                routeRequest.Groups.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
            };
            Parallel.For(0, routeRequest.Groups.Count, parallelOptions,
                (groupIndex, candidatePoolLoopState) =>
                {
                    var sourceCandidatePool = sourceCandidatePools?[groupIndex];
                    if (sourceCandidatePool?.ValidCandidates.Count > 0)
                    {
                        expandedCandidatePools[groupIndex] = sourceCandidatePool;
                        return;
                    }

                    var routeGroupRequest = routeRequest.Groups[groupIndex];
                    var candidatePoolsToMerge = new List<LinkageRouteCandidatePool>
                    {
                        sourceCandidatePool
                    };
                    foreach (var traversalDirection in new[]
                    {
                        LinkageRouteTraversalDirection.Forward,
                        LinkageRouteTraversalDirection.Reverse
                    })
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        candidatePoolsToMerge.Add(
                            LinkageRouteCandidateFactory
                                .BuildExtendedClearanceSeedPool(
                                    routeRequest,
                                    routeGroupRequest,
                                    fixedTerminalAndObstacleSolids,
                                    traversalDirection,
                                    cancellationToken));
                    }

                    expandedCandidatePools[groupIndex] =
                        LinkageRouteCandidateFactory.MergeTraversalPools(
                            routeRequest,
                            candidatePoolsToMerge);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        candidatePoolLoopState.Stop();
                    }
                });
            return expandedCandidatePools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool> BuildCandidatePools(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteAlgorithmKind algorithmKind,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var candidatePools = new LinkageRouteCandidatePool[routeRequest.Groups.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(routeRequest)
            };
            Parallel.For(0, routeRequest.Groups.Count, parallelOptions,
                (groupIndex, candidatePoolLoopState) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    candidatePoolLoopState.Stop();
                    return;
                }

                try
                {
                    candidatePools[groupIndex] = LinkageRouteCandidateFactory.BuildPool(
                        routeRequest,
                        routeRequest.Groups[groupIndex],
                        fixedTerminalAndObstacleSolids,
                        algorithmKind == LinkageRouteAlgorithmKind.SweptRepair,
                        traversalDirection,
                        cancellationToken);
                }
                catch (OperationCanceledException poolCancellation) when (
                    cancellationToken.IsCancellationRequested &&
                    poolCancellation.CancellationToken == cancellationToken)
                {
                    // Preserve completed sibling pools under the existing
                    // manual-stop contract. Only this owner's cancellation
                    // is consumed; unrelated failures must still propagate.
                    candidatePoolLoopState.Stop();
                    return;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    candidatePoolLoopState.Stop();
                }
            });
            return candidatePools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            MergeTraversalCandidatePools(
                LinkageMultiGroupRouteRequest routeRequest,
                IReadOnlyList<LinkageRouteCandidatePool> forwardCandidatePools,
                IReadOnlyList<LinkageRouteCandidatePool> reverseCandidatePools,
                bool preserveCompletedCandidates = false)
        {
            var mergedCandidatePools = new List<LinkageRouteCandidatePool>();
            for (var routeGroupIndex = 0;
                routeGroupIndex < routeRequest.Groups.Count;
                routeGroupIndex++)
            {
                mergedCandidatePools.Add(
                    LinkageRouteCandidateFactory.MergeTraversalPools(
                        routeRequest,
                        new[]
                        {
                            forwardCandidatePools?[routeGroupIndex],
                            reverseCandidatePools?[routeGroupIndex]
                        }, preserveCompletedCandidates));
            }

            return mergedCandidatePools;
        }

        private static IReadOnlyList<LinkageRouteCandidatePool> MergeFiniteAndFullCandidatePools(
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteCandidatePool> fullCandidatePools,
            IReadOnlyList<LinkageRouteCandidatePool> finiteCandidatePools,
            bool preserveCompletedCandidates = false)
        {
            return finiteCandidatePools == null ? fullCandidatePools :
                MergeTraversalCandidatePools(routeRequest, fullCandidatePools, finiteCandidatePools,
                    preserveCompletedCandidates);
        }

        private static void WaitForSectionSearchTasks(IReadOnlyList<Task> sectionTasks,
            CancellationTokenSource sectionOwner)
        {
            while (sectionTasks.Any(task => !task.IsCompleted))
            {
                if (sectionTasks.Any(task => task.IsFaulted || task.IsCanceled))
                    sectionOwner.Cancel();
                var pendingTasks = sectionTasks.Where(task => !task.IsCompleted).ToArray();
                if (pendingTasks.Length > 0) Task.WaitAny(pendingTasks, 50);
            }
            try
            {
                Task.WaitAll(sectionTasks.ToArray());
            }
            catch (AggregateException completionFailure) when (
                sectionOwner.IsCancellationRequested &&
                completionFailure.Flatten().InnerExceptions.All(failure =>
                    failure is OperationCanceledException canceled &&
                    canceled.CancellationToken == sectionOwner.Token))
            {
                // Only this section owner's cancellation is normal stop. Foreign
                // tokens and real faults propagate after every sibling is stopped.
            }
        }

        private static int ResolveMaximumDegreeOfParallelism(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return Math.Max(
                1,
                Math.Min(
                    Math.Max(
                        1,
                        System.Environment.ProcessorCount -
                            Math.Max(3, routeRequest.SearchOptions
                                .SchedulingAndPublishingWorkerReserve)),
                    routeRequest.SearchOptions.MaximumDegreeOfParallelism));
        }

        // 同时构建 activeGroupCount 个组时，每组可提前并行构建的骨架数；
        // 总量不超过原并行度，单组为空时可用满全部名额。
        private static int ResolveGroupPrefetchWindow(
            LinkageMultiGroupRouteRequest routeRequest,
            int activeGroupCount)
        {
            return Math.Max(
                1,
                ResolveMaximumDegreeOfParallelism(routeRequest) /
                    Math.Max(1, activeGroupCount));
        }

        private static int ResolveSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var availableParallelWorkerCount =
                ResolveMaximumDegreeOfParallelism(routeRequest);
            var ratioLimitedWorkerCount = Math.Max(
                1,
                (int)Math.Floor(
                    availableParallelWorkerCount * routeRequest.SearchOptions
                        .InitialSolutionOptimizationWorkerRatio));
            return Math.Max(
                1,
                Math.Min(
                    routeRequest.SearchOptions
                        .MaximumInitialSolutionOptimizationWorkerCount,
                    ratioLimitedWorkerCount));
        }

        private static int ResolveInitialMainSearchWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var availableParallelWorkerCount =
                ResolveMaximumDegreeOfParallelism(routeRequest);
            return Math.Max(
                1,
                availableParallelWorkerCount -
                    ResolveSolutionOptimizationWorkerCount(routeRequest));
        }

        private static int ResolvePostSwitchMainSearchWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var availableParallelWorkerCount =
                ResolveMaximumDegreeOfParallelism(routeRequest);
            return Math.Max(
                1,
                Math.Min(
                    availableParallelWorkerCount - 1,
                    (int)Math.Ceiling(
                        availableParallelWorkerCount * routeRequest
                            .SearchOptions.PostSwitchMainSearchWorkerRatio)));
        }

        private static int ResolvePostSwitchSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var availableParallelWorkerCount =
                ResolveMaximumDegreeOfParallelism(routeRequest);
            var retainedMainSearchWorkerCount =
                ResolvePostSwitchMainSearchWorkerCount(routeRequest);
            var ratioOptimizationWorkerCount = Math.Max(
                1,
                (int)Math.Floor(
                    availableParallelWorkerCount * routeRequest.SearchOptions
                        .PostSwitchSolutionOptimizationWorkerRatio));
            var availableOptimizationWorkerCount = Math.Max(
                1,
                availableParallelWorkerCount - retainedMainSearchWorkerCount);
            return Math.Max(
                1,
                Math.Min(
                    availableOptimizationWorkerCount,
                    Math.Max(
                        ResolveSolutionOptimizationWorkerCount(routeRequest),
                        ratioOptimizationWorkerCount)));
        }

        private static int ResolveStagnationMainSearchWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return Math.Max(
                1,
                Math.Min(
                    ResolveMaximumDegreeOfParallelism(routeRequest),
                    routeRequest.SearchOptions.StagnationMainSearchWorkerCount));
        }

        private static int ResolveStagnationSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var availableParallelWorkerCount =
                ResolveMaximumDegreeOfParallelism(routeRequest);
            return Math.Max(
                1,
                availableParallelWorkerCount -
                    ResolveStagnationMainSearchWorkerCount(routeRequest));
        }

        private static int ResolveDeepSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var solutionOptimizationWorkerCount =
                ResolveSolutionOptimizationWorkerCount(routeRequest);
            // 总优化并发只有一路时快速、深度任务只能共用该路；两路及以上
            // 始终为快速直接几何优化保留一路，避免长耗时深度任务阻塞首个优化结果。
            return Math.Max(
                1,
                solutionOptimizationWorkerCount - routeRequest.SearchOptions
                    .FastSolutionOptimizationWorkerReserve);
        }

        private static int ResolvePostSwitchDeepSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var postSwitchSolutionOptimizationWorkerCount =
                ResolvePostSwitchSolutionOptimizationWorkerCount(routeRequest);
            return Math.Max(
                1,
                postSwitchSolutionOptimizationWorkerCount - routeRequest
                    .SearchOptions.FastSolutionOptimizationWorkerReserve);
        }

        private static int ResolveStagnationDeepSolutionOptimizationWorkerCount(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return Math.Max(
                1,
                ResolveStagnationSolutionOptimizationWorkerCount(routeRequest) -
                    routeRequest.SearchOptions.FastSolutionOptimizationWorkerReserve);
        }

        private static void SearchCombinations(
            SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> remainingCandidatePools,
            IList<LinkageRouteCandidate> selectedCandidates,
            CombinationSearchStage searchStage)
        {
            if (searchStage.ShouldStop)
            {
                return;
            }

            if (remainingCandidatePools.Count == 0)
            {
                var completedCandidateSet = selectedCandidates.ToList();
                if (searchContext.HasPublishedCompleteTopology(
                    completedCandidateSet))
                {
                    return;
                }

                var addedSuccessfulSolution =
                    searchContext.TryAddCompleteSolution(
                        completedCandidateSet,
                        searchStageCancellationToken: searchStage.CancellationToken);
                if (addedSuccessfulSolution)
                {
                    searchStage.NotifySuccessfulSolution();
                }
                return;
            }

            if (!searchContext.TryVisitState(selectedCandidates) ||
                searchContext.ShouldPrune(
                    selectedCandidates,
                    remainingCandidatePools))
            {
                return;
            }

            // 无论是否开放全部组顺序，都先处理与当前已选路径兼容候选最少的组。
            // 深层修复会动态生成绕障候选；若仍按空间接近程度排序，容易组会先形成
            // 大量分支，而真正零兼容边的困难组只能在时间末尾才获得修复机会。
            // 最受约束组优先只调整遍历顺序；深层仍保留其余全部组顺序作为兜底。
            var orderedRemainingCandidatePools =
                OrderRemainingPoolsByCurrentConstraint(
                    searchContext,
                    remainingCandidatePools,
                    selectedCandidates,
                    searchStage);
            if (!searchStage.ExploreAllPoolOrders &&
                orderedRemainingCandidatePools.Count > 1)
            {
                orderedRemainingCandidatePools = orderedRemainingCandidatePools
                    .Take(1)
                    .ToList();
            }

            foreach (var candidatePool in orderedRemainingCandidatePools)
            {
                if (searchStage.ShouldStop)
                {
                    return;
                }

                var nextRemainingCandidatePools = remainingCandidatePools
                    .Where(remainingCandidatePool => !ReferenceEquals(
                        remainingCandidatePool,
                        candidatePool))
                    .ToList();
                var orderedBaseCandidates = searchContext
                    .GetOrderedCandidates(candidatePool);
                var knownCandidateSignatures = new HashSet<string>(
                    orderedBaseCandidates.Select(
                        LinkageRouteCandidateFactory.GetCandidateSignature),
                    StringComparer.Ordinal);
                var hasBaseClearanceFailure = false;
                LinkageRouteCandidate closestPairViolationCandidate = null;
                PairClearanceEvaluation closestPairViolationEvaluation = null;
                foreach (var routeCandidate in orderedBaseCandidates)
                {
                    if (nextRemainingCandidatePools.Count == 0 &&
                        searchContext.HasPublishedCompleteTopology(
                            selectedCandidates.Concat(new[] { routeCandidate })))
                    {
                        continue;
                    }

                    Interlocked.Increment(ref searchContext.ProcessedStateCount);
                    var pairClearanceEvaluation = searchContext.EvaluateAgainstSelected(
                        routeCandidate,
                        selectedCandidates,
                        searchStage.CancellationToken);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }

                    if (!pairClearanceEvaluation.IsClear)
                    {
                        hasBaseClearanceFailure = true;
                        if (IsBetterPairViolationRepairSource(
                            routeCandidate,
                            pairClearanceEvaluation,
                            closestPairViolationCandidate,
                            closestPairViolationEvaluation))
                        {
                            closestPairViolationCandidate = routeCandidate;
                            closestPairViolationEvaluation = pairClearanceEvaluation;
                        }
                        searchContext.RecordFailure(
                            selectedCandidates,
                            routeCandidate,
                            pairClearanceEvaluation,
                            "新增逻辑组与已选路径的三维绝缘距离不足。" );

                        continue;
                    }

                    selectedCandidates.Add(routeCandidate);
                    SearchCombinations(
                        searchContext,
                        nextRemainingCandidatePools,
                        selectedCandidates,
                        searchStage);
                    selectedCandidates.RemoveAt(selectedCandidates.Count - 1);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }
                }

                if (closestPairViolationCandidate != null &&
                    searchStage.AllowPairViolationDirectedCandidates &&
                    searchContext.AlgorithmKind ==
                        LinkageRouteAlgorithmKind.SweptRepair)
                {
                    SearchPairViolationDirectedCandidates(
                        searchContext,
                        closestPairViolationCandidate,
                        closestPairViolationEvaluation,
                        nextRemainingCandidatePools,
                        selectedCandidates,
                        knownCandidateSignatures,
                        searchStage);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }

                    SearchSelectedPairViolationDirectedCandidates(
                        searchContext,
                        closestPairViolationCandidate,
                        closestPairViolationEvaluation,
                        nextRemainingCandidatePools,
                        selectedCandidates,
                        searchStage);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }
                }

                if (hasBaseClearanceFailure &&
                    searchStage.AllowClearanceExpansionCandidates &&
                    searchContext.AlgorithmKind ==
                        LinkageRouteAlgorithmKind.SweptRepair)
                {
                    // 基础候选先按实际长度全部搜索；只有确实出现相间冲突时，
                    // 才在其后生成临界绕障候选。这里只改变搜索次序，不删除候选，
                    // 因而首个普通可行方案不会被昂贵的2～8段扩展阻塞。
                    SearchClearanceExpansionCandidates(
                        searchContext,
                        candidatePool,
                        nextRemainingCandidatePools,
                        selectedCandidates,
                        knownCandidateSignatures,
                        searchStage);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }
                }
            }
        }

        private static bool IsBetterPairViolationRepairSource(
            LinkageRouteCandidate candidateRoute,
            PairClearanceEvaluation candidatePairEvaluation,
            LinkageRouteCandidate currentRoute,
            PairClearanceEvaluation currentPairEvaluation)
        {
            if (currentRoute == null || currentPairEvaluation == null)
            {
                return true;
            }

            if (candidatePairEvaluation.ViolationCount !=
                currentPairEvaluation.ViolationCount)
            {
                return candidatePairEvaluation.ViolationCount <
                    currentPairEvaluation.ViolationCount;
            }

            if (Math.Abs(candidatePairEvaluation.MaximumViolationDepth -
                    currentPairEvaluation.MaximumViolationDepth) > 1e-8d)
            {
                return candidatePairEvaluation.MaximumViolationDepth <
                    currentPairEvaluation.MaximumViolationDepth;
            }

            if (Math.Abs(candidatePairEvaluation.MinimumClearance -
                    currentPairEvaluation.MinimumClearance) > 1e-8d)
            {
                return candidatePairEvaluation.MinimumClearance >
                    currentPairEvaluation.MinimumClearance;
            }

            return string.CompareOrdinal(
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        candidateRoute),
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        currentRoute)) < 0;
        }

        private static void SearchPairViolationDirectedCandidates(
            SearchContext searchContext,
            LinkageRouteCandidate sourceRouteCandidate,
            PairClearanceEvaluation sourcePairEvaluation,
            IReadOnlyList<LinkageRouteCandidatePool> nextRemainingCandidatePools,
            IList<LinkageRouteCandidate> selectedCandidates,
            ISet<string> knownCandidateSignatures,
            CombinationSearchStage searchStage)
        {
            if (searchStage.ShouldStop ||
                sourcePairEvaluation?.SuggestedSeparationDirection == null ||
                sourcePairEvaluation.SuggestedSeparationDistance <= 1e-8d)
            {
                return;
            }

            foreach (var directedRepairCandidate in searchContext
                .GetPairViolationDirectedCandidates(
                    sourceRouteCandidate,
                    selectedCandidates,
                    sourcePairEvaluation,
                    searchStage.CancellationToken))
            {
                if (searchStage.ShouldStop)
                {
                    return;
                }

                var directedRepairCandidateSignature =
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        directedRepairCandidate);
                if (!knownCandidateSignatures.Add(
                    directedRepairCandidateSignature))
                {
                    continue;
                }

                if (nextRemainingCandidatePools.Count == 0 &&
                    searchContext.HasPublishedCompleteTopology(
                        selectedCandidates.Concat(
                            new[] { directedRepairCandidate })))
                {
                    continue;
                }

                Interlocked.Increment(ref searchContext.ProcessedStateCount);
                var directedRepairPairEvaluation = searchContext
                    .EvaluateAgainstSelected(
                        directedRepairCandidate,
                        selectedCandidates,
                        searchStage.CancellationToken);
                if (searchStage.ShouldStop)
                {
                    return;
                }

                if (!directedRepairPairEvaluation.IsClear)
                {
                    searchContext.RecordFailure(
                        selectedCandidates,
                        directedRepairCandidate,
                        directedRepairPairEvaluation,
                        "按真实冲突方向执行极限位移后仍未满足相间三维绝缘距离。" );
                    continue;
                }

                selectedCandidates.Add(directedRepairCandidate);
                try
                {
                    SearchCombinations(
                        searchContext,
                        nextRemainingCandidatePools,
                        selectedCandidates,
                        searchStage);
                }
                finally
                {
                    selectedCandidates.RemoveAt(selectedCandidates.Count - 1);
                }
            }
        }

        private static void SearchSelectedPairViolationDirectedCandidates(
            SearchContext searchContext,
            LinkageRouteCandidate failedRouteCandidate,
            PairClearanceEvaluation failedPairEvaluation,
            IReadOnlyList<LinkageRouteCandidatePool> nextRemainingCandidatePools,
            IList<LinkageRouteCandidate> selectedCandidates,
            CombinationSearchStage searchStage)
        {
            if (searchStage.ShouldStop ||
                failedPairEvaluation?.SuggestedSeparationDirection == null ||
                string.IsNullOrWhiteSpace(
                    failedPairEvaluation.ConflictingGroupKey))
            {
                return;
            }

            var conflictingSelectedCandidateIndex = selectedCandidates
                .Select((selectedCandidate, selectedCandidateIndex) => new
                {
                    SelectedCandidate = selectedCandidate,
                    SelectedCandidateIndex = selectedCandidateIndex
                })
                .Where(selectedCandidateEntry => string.Equals(
                    selectedCandidateEntry.SelectedCandidate.GroupRequest.GroupKey,
                    failedPairEvaluation.ConflictingGroupKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(selectedCandidateEntry =>
                    selectedCandidateEntry.SelectedCandidateIndex)
                .DefaultIfEmpty(-1)
                .First();
            if (conflictingSelectedCandidateIndex < 0)
            {
                return;
            }

            var conflictingSelectedCandidate = selectedCandidates[
                conflictingSelectedCandidateIndex];
            var oppositeSeparationDirection = new Vector3d(
                -failedPairEvaluation.SuggestedSeparationDirection.X,
                -failedPairEvaluation.SuggestedSeparationDirection.Y,
                -failedPairEvaluation.SuggestedSeparationDirection.Z);
            var selectedCandidateRepairEvaluation = new PairClearanceEvaluation
            {
                IsClear = false,
                MaximumViolationDepth = failedPairEvaluation.MaximumViolationDepth,
                SuggestedSeparationDistance =
                    failedPairEvaluation.SuggestedSeparationDistance,
                SuggestedSeparationDirection = oppositeSeparationDirection
            };
            var selectedCandidateRepairObstacles = selectedCandidates
                .Where((selectedCandidate, selectedCandidateIndex) =>
                    selectedCandidateIndex != conflictingSelectedCandidateIndex)
                .Concat(new[] { failedRouteCandidate })
                .ToList();
            var knownSelectedRepairSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var selectedRepairCandidate in searchContext
                .GetPairViolationDirectedCandidates(
                    conflictingSelectedCandidate,
                    selectedCandidateRepairObstacles,
                    selectedCandidateRepairEvaluation,
                    searchStage.CancellationToken))
            {
                if (searchStage.ShouldStop)
                {
                    return;
                }

                var selectedRepairCandidateSignature =
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        selectedRepairCandidate);
                if (!knownSelectedRepairSignatures.Add(
                    selectedRepairCandidateSignature))
                {
                    continue;
                }

                selectedCandidates[conflictingSelectedCandidateIndex] =
                    selectedRepairCandidate;
                try
                {
                    Interlocked.Increment(ref searchContext.ProcessedStateCount);
                    var failedCandidateAfterSelectedRepairEvaluation = searchContext
                        .EvaluateAgainstSelected(
                            failedRouteCandidate,
                            selectedCandidates,
                            searchStage.CancellationToken);
                    if (searchStage.ShouldStop)
                    {
                        return;
                    }

                    if (!failedCandidateAfterSelectedRepairEvaluation.IsClear)
                    {
                        searchContext.RecordFailure(
                            selectedCandidates,
                            failedRouteCandidate,
                            failedCandidateAfterSelectedRepairEvaluation,
                            "反向移动冲突组到极限距离后仍未满足相间三维绝缘距离。" );
                        continue;
                    }

                    selectedCandidates.Add(failedRouteCandidate);
                    try
                    {
                        SearchCombinations(
                            searchContext,
                            nextRemainingCandidatePools,
                            selectedCandidates,
                            searchStage);
                    }
                    finally
                    {
                        selectedCandidates.RemoveAt(
                            selectedCandidates.Count - 1);
                    }
                }
                finally
                {
                    selectedCandidates[conflictingSelectedCandidateIndex] =
                        conflictingSelectedCandidate;
                }
            }
        }

        private static void SearchClearanceExpansionCandidates(
            SearchContext searchContext,
            LinkageRouteCandidatePool candidatePool,
            IReadOnlyList<LinkageRouteCandidatePool> nextRemainingCandidatePools,
            IList<LinkageRouteCandidate> selectedCandidates,
            ISet<string> knownCandidateSignatures,
            CombinationSearchStage searchStage)
        {
            if (searchStage.ShouldStop)
            {
                return;
            }

            foreach (var expandedRouteCandidate in searchContext
                .GetClearanceExpansionCandidates(
                    candidatePool,
                    selectedCandidates,
                    searchStage.AllowDeepClearanceExpansionCandidates,
                    searchStage.CancellationToken))
            {
                if (searchStage.ShouldStop)
                {
                    return;
                }

                searchContext.RegisterGeneratedOptimizationCandidate(
                    expandedRouteCandidate);

                var expandedCandidateSignature =
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        expandedRouteCandidate);
                if (!knownCandidateSignatures.Add(expandedCandidateSignature))
                {
                    continue;
                }

                if (nextRemainingCandidatePools.Count == 0 &&
                    searchContext.HasPublishedCompleteTopology(
                        selectedCandidates.Concat(
                            new[] { expandedRouteCandidate })))
                {
                    continue;
                }

                Interlocked.Increment(ref searchContext.ProcessedStateCount);
                var expandedPairEvaluation = searchContext.EvaluateAgainstSelected(
                    expandedRouteCandidate,
                    selectedCandidates,
                    searchStage.CancellationToken);
                if (searchStage.ShouldStop)
                {
                    return;
                }

                if (!expandedPairEvaluation.IsClear)
                {
                    searchContext.RecordFailure(
                        selectedCandidates,
                        expandedRouteCandidate,
                        expandedPairEvaluation,
                        "绝缘临界层扩展路径仍未满足相间三维绝缘距离。" );
                    continue;
                }

                selectedCandidates.Add(expandedRouteCandidate);
                SearchCombinations(
                    searchContext,
                    nextRemainingCandidatePools,
                    selectedCandidates,
                    searchStage);
                selectedCandidates.RemoveAt(selectedCandidates.Count - 1);
            }
        }

        private static List<LinkageRouteCandidatePool>
            OrderRemainingPoolsByCurrentConstraint(
            SearchContext searchContext,
            IEnumerable<LinkageRouteCandidatePool> candidatePools,
            IList<LinkageRouteCandidate> selectedCandidates,
            CombinationSearchStage searchStage)
        {
            var constrainedCandidatePools = new List<Tuple<
                LinkageRouteCandidatePool,
                int,
                double>>();
            foreach (var candidatePool in candidatePools)
            {
                if (searchStage.ShouldStop)
                {
                    break;
                }

                var compatibleCandidateCount = 0;
                foreach (var routeCandidate in OrderCandidates(
                    candidatePool.ValidCandidates,
                    searchContext.AlgorithmKind,
                    searchContext.RouteRequest.SearchOptions.BeamWidth))
                {
                    var pairClearanceEvaluation = searchContext
                        .EvaluateAgainstSelected(
                            routeCandidate,
                            selectedCandidates,
                            searchStage.CancellationToken);
                    if (searchStage.ShouldStop)
                    {
                        break;
                    }

                    if (pairClearanceEvaluation.IsClear)
                    {
                        compatibleCandidateCount++;
                    }
                }

                constrainedCandidatePools.Add(Tuple.Create(
                    candidatePool,
                    compatibleCandidateCount,
                    GetPoolProximity(
                        selectedCandidates.LastOrDefault(),
                        candidatePool)));
                if (compatibleCandidateCount == 0 &&
                    !searchStage.ExploreAllPoolOrders)
                {
                    // 单顺序层可优先返回最受约束组；全部顺序层必须保留其余组。
                    break;
                }
            }

            return constrainedCandidatePools
                .OrderBy(candidatePoolTuple => candidatePoolTuple.Item2)
                .ThenBy(candidatePoolTuple => candidatePoolTuple.Item3)
                .ThenBy(candidatePoolTuple =>
                    candidatePoolTuple.Item1.GroupRequest.GroupKey,
                    StringComparer.Ordinal)
                .Select(candidatePoolTuple => candidatePoolTuple.Item1)
                .ToList();
        }

        private static IReadOnlyList<LinkageRouteCandidatePool>
            OrderRemainingPoolsByProximity(
                IEnumerable<LinkageRouteCandidatePool> candidatePools,
                IList<LinkageRouteCandidate> selectedCandidates)
        {
            var latestSelectedCandidate = selectedCandidates.LastOrDefault();
            return candidatePools
                .OrderBy(candidatePool => GetPoolProximity(
                    latestSelectedCandidate,
                    candidatePool))
                .ThenBy(candidatePool => candidatePool.GroupRequest.GroupKey,
                    StringComparer.Ordinal)
                .ToList();
        }

        private static double GetPoolProximity(
            LinkageRouteCandidate selectedCandidate,
            LinkageRouteCandidatePool candidatePool)
        {
            if (selectedCandidate?.ScanSolids == null ||
                selectedCandidate.ScanSolids.Count == 0)
            {
                return 0d;
            }

            var terminalAnchors = new[]
            {
                candidatePool.GroupRequest.StartTerminal.RouteAnchor,
                candidatePool.GroupRequest.EndTerminal.RouteAnchor
            };
            var minimumDistance = double.PositiveInfinity;
            foreach (var selectedSolid in selectedCandidate.ScanSolids)
            {
                var selectedSolidExtents = selectedSolid.GetAxisAlignedExtents();
                var minimumX = selectedSolid.Center.X - selectedSolidExtents[0];
                var maximumX = selectedSolid.Center.X + selectedSolidExtents[0];
                var minimumY = selectedSolid.Center.Y - selectedSolidExtents[1];
                var maximumY = selectedSolid.Center.Y + selectedSolidExtents[1];
                var minimumZ = selectedSolid.Center.Z - selectedSolidExtents[2];
                var maximumZ = selectedSolid.Center.Z + selectedSolidExtents[2];
                foreach (var terminalAnchor in terminalAnchors)
                {
                    var xGap = GetPointIntervalGap(terminalAnchor.X, minimumX, maximumX);
                    var yGap = GetPointIntervalGap(terminalAnchor.Y, minimumY, maximumY);
                    var zGap = GetPointIntervalGap(terminalAnchor.Z, minimumZ, maximumZ);
                    minimumDistance = Math.Min(
                        minimumDistance,
                        Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap));
                }
            }

            return minimumDistance;
        }

        private static double GetPointIntervalGap(
            double pointCoordinate,
            double intervalMinimum,
            double intervalMaximum)
        {
            if (pointCoordinate < intervalMinimum)
            {
                return intervalMinimum - pointCoordinate;
            }

            return pointCoordinate > intervalMaximum
                ? pointCoordinate - intervalMaximum
                : 0d;
        }

        private static LinkageRouteSolution CreateSuccessSolution(
            SearchContext searchContext,
            IEnumerable<LinkageRouteCandidate> selectedCandidates)
        {
            return CreateCompleteSolution(
                searchContext,
                selectedCandidates,
                true);
        }

        private static LinkageRouteSolution CreateCompleteSolution(
            SearchContext searchContext,
            IEnumerable<LinkageRouteCandidate> selectedCandidates,
            bool requireRequestedInsulationClearance)
        {
            var selectedCandidateList = selectedCandidates.ToList();
            var candidateByGroupKey = selectedCandidateList.ToDictionary(
                candidate => candidate.GroupRequest.GroupKey,
                StringComparer.OrdinalIgnoreCase);
            var solution = new LinkageRouteSolution
            {
                AlgorithmKind = searchContext.AlgorithmKind,
                IsSuccess = true,
                MinimumClearance = double.PositiveInfinity
            };
            foreach (var routeGroupRequest in searchContext.RouteRequest.Groups)
            {
                var selectedCandidate = candidateByGroupKey[routeGroupRequest.GroupKey];
                if (!LinkageRouteTerminalAccessoryBuilder.TryValidateConnectionOutput(
                        selectedCandidate?.PlanarConversion,
                        searchContext.RouteRequest,
                        routeGroupRequest,
                        out _))
                {
                    return null;
                }
                var groupSolution = selectedCandidate.ToGroupSolution();
                solution.GroupSolutions.Add(groupSolution);
                solution.ActualCopperLength += groupSolution.ActualCopperLength;
                solution.PerpendicularLapCount += groupSolution.PerpendicularLapCount;
                foreach (var outputGeometryDiagnostic in selectedCandidate
                    .PlanarConversion.Diagnostics
                    .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
                    .Distinct(StringComparer.Ordinal))
                {
                    solution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
                    {
                        GroupKey = routeGroupRequest.GroupKey,
                        Stage = LinkageRouteFailureStage.Conversion,
                        Message = outputGeometryDiagnostic
                    });
                }
                var exactStaticMinimumDistance =
                    searchContext.GetExactStaticMinimumDistance(selectedCandidate);
                solution.MinimumClearance = Math.Min(
                    solution.MinimumClearance,
                    exactStaticMinimumDistance);
                var requiredStaticInsulationClearance = selectedCandidate
                    .GroupRequest
                    .GetRequiredInsulationClearance(searchContext.RouteRequest);
                if (requireRequestedInsulationClearance &&
                    !IsFinalRequestedClearanceSatisfied(
                        exactStaticMinimumDistance, requiredStaticInsulationClearance))
                {
                    searchContext.RecordFinalExactClearanceFailure(
                        selectedCandidateList,
                        selectedCandidate,
                        exactStaticMinimumDistance,
                        requiredStaticInsulationClearance,
                        "<固定排或障碍物>");
                    return null;
                }
            }

            // 方案进入后续排序、发布或方案库前，先确认实际叠排结构和
            // 连接排派生结果完整；绝缘距离仍由下面的专门校验负责。
            if (!TryValidatePhysicalSolutionMaterialization(
                    searchContext.RouteRequest,
                    solution))
            {
                return null;
            }

            for (var firstCandidateIndex = 0;
                firstCandidateIndex < selectedCandidateList.Count;
                firstCandidateIndex++)
            {
                for (var secondCandidateIndex = firstCandidateIndex + 1;
                    secondCandidateIndex < selectedCandidateList.Count;
                    secondCandidateIndex++)
                {
                    var firstSelectedCandidate = selectedCandidateList[
                        firstCandidateIndex];
                    var secondSelectedCandidate = selectedCandidateList[
                        secondCandidateIndex];
                    var exactPairMinimumDistance =
                        searchContext.GetExactPairMinimumDistance(
                            firstSelectedCandidate,
                            secondSelectedCandidate);
                    solution.MinimumClearance = Math.Min(
                        solution.MinimumClearance,
                        exactPairMinimumDistance);
                    var requiredPairInsulationClearance = Math.Max(
                        firstSelectedCandidate.GroupRequest
                            .GetRequiredInsulationClearance(
                                searchContext.RouteRequest),
                        secondSelectedCandidate.GroupRequest
                            .GetRequiredInsulationClearance(
                                searchContext.RouteRequest));
                    if (!requireRequestedInsulationClearance ||
                        IsFinalRequestedClearanceSatisfied(
                            exactPairMinimumDistance, requiredPairInsulationClearance))
                    {
                        continue;
                    }

                    searchContext.RecordFinalExactClearanceFailure(
                        selectedCandidateList,
                        secondSelectedCandidate,
                        exactPairMinimumDistance,
                        requiredPairInsulationClearance,
                        firstSelectedCandidate.GroupRequest.GroupKey);
                    return null;
                }
            }

            return solution;
        }

        private static bool IsFinalRequestedClearanceSatisfied(
            double actualMinimumClearance, double requiredInsulationClearance)
        {
            // 精确距离保留原值；方案构建与发布终检按同一毫米验收口径判断，
            // 不能以计算 epsilon 把尺寸容差内的候选再次拒绝。
            return actualMinimumClearance +
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison >=
                requiredInsulationClearance;
        }

        private static LinkageRouteSolution CreatePoolFailure(
            LinkageRouteAlgorithmKind algorithmKind,
            IEnumerable<LinkageRouteCandidatePool> candidatePools,
            LinkageRouteCandidatePool emptyPool,
            long elapsedMilliseconds)
        {
            var candidatePoolList = candidatePools.ToList();
            var completedCandidates = candidatePoolList
                .TakeWhile(candidatePool => candidatePool != emptyPool)
                .Select(candidatePool => candidatePool.ValidCandidates.FirstOrDefault())
                .Where(candidate => candidate != null)
                .ToList();
            var failedCandidate = emptyPool.BestFailedCandidate;
            var failureSolution = CreateCombinationFailure(
                algorithmKind,
                completedCandidates,
                failedCandidate,
                completedCandidates.Count,
                failedCandidate?.ViolationCount ?? 1,
                failedCandidate?.MaximumViolationDepth ?? 1d,
                FormatEmptyPoolDiagnostic(emptyPool));
            foreach (var additionalEmptyPool in candidatePoolList.Where(candidatePool =>
                candidatePool != emptyPool && candidatePool.ValidCandidates.Count == 0))
            {
                var additionalFailedCandidate =
                    additionalEmptyPool.BestFailedCandidate;
                failureSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
                {
                    GroupKey = additionalEmptyPool.GroupRequest?.GroupKey,
                    Stage = additionalFailedCandidate?.FailureStage ??
                        LinkageRouteFailureStage.AxisSkeleton,
                    Message = FormatEmptyPoolDiagnostic(additionalEmptyPool),
                    CompletedGroupCount = candidatePoolList.Count(candidatePool =>
                        candidatePool.ValidCandidates.Count > 0),
                    ViolationCount = additionalFailedCandidate?.ViolationCount ?? 1,
                    MaximumViolationDepth =
                        additionalFailedCandidate?.MaximumViolationDepth ?? 1d,
                    PreviewCenterline =
                        additionalFailedCandidate?.ManufacturableCenterline ??
                        new List<Point3d>()
                });
            }

            return failureSolution;
        }

        private static string FormatEmptyPoolDiagnostic(
            LinkageRouteCandidatePool emptyCandidatePool)
        {
            var stageSummaries = emptyCandidatePool.FailureStageCounts
                .OrderBy(stageCountPair => stageCountPair.Key)
                .Select(stageCountPair =>
                {
                    emptyCandidatePool.RepresentativeFailedCandidates.TryGetValue(
                        stageCountPair.Key,
                        out var representativeFailure);
                    emptyCandidatePool.FailureDiagnosticSamples.TryGetValue(
                        stageCountPair.Key,
                        out var failureDiagnosticSamples);
                    var diagnosticSampleText = failureDiagnosticSamples != null &&
                        failureDiagnosticSamples.Count > 0
                            ? string.Join(" | ", failureDiagnosticSamples)
                            : representativeFailure?.Diagnostic ?? "无诊断";
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}={1}（示例：{2}）",
                        stageCountPair.Key,
                        stageCountPair.Value,
                        diagnosticSampleText);
                });
            var baseDiagnostic = emptyCandidatePool.Diagnostics.FirstOrDefault() ??
                "逻辑组没有有效候选。";
            return baseDiagnostic + " 失败阶段分布：" +
                string.Join("；", stageSummaries);
        }

        private static LinkageRouteSolution CreateCombinationFailure(
            LinkageRouteAlgorithmKind algorithmKind,
            IEnumerable<LinkageRouteCandidate> completedCandidates,
            LinkageRouteCandidate failedCandidate,
            int completedGroupCount,
            int violationCount,
            double maximumViolationDepth,
            string diagnosticMessage)
        {
            var failureSolution = new LinkageRouteSolution
            {
                AlgorithmKind = algorithmKind,
                IsSuccess = false,
                MinimumClearance = 0d
            };
            foreach (var completedCandidate in completedCandidates)
            {
                var groupSolution = completedCandidate.ToGroupSolution();
                failureSolution.GroupSolutions.Add(groupSolution);
                failureSolution.ActualCopperLength += groupSolution.ActualCopperLength;
                failureSolution.PerpendicularLapCount += groupSolution.PerpendicularLapCount;
            }

            if (failedCandidate?.PlanarConversion?.IsValid == true)
            {
                var failedGroupSolution = failedCandidate.ToGroupSolution();
                failureSolution.GroupSolutions.Add(failedGroupSolution);
                failureSolution.ActualCopperLength += failedGroupSolution.ActualCopperLength;
                failureSolution.PerpendicularLapCount += failedGroupSolution.PerpendicularLapCount;
            }

            failureSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
            {
                GroupKey = failedCandidate?.GroupRequest?.GroupKey,
                Stage = failedCandidate?.FailureStage == LinkageRouteFailureStage.None
                    ? LinkageRouteFailureStage.Clearance
                    : failedCandidate?.FailureStage ?? LinkageRouteFailureStage.AxisSkeleton,
                Message = diagnosticMessage,
                CompletedGroupCount = completedGroupCount,
                ViolationCount = violationCount,
                MaximumViolationDepth = maximumViolationDepth,
                PreviewCenterline = failedCandidate?.ManufacturableCenterline ??
                    new List<Point3d>()
            });
            return failureSolution;
        }

        private static LinkageRouteSolution CreateInterruptedSolution(
            LinkageRouteAlgorithmKind algorithmKind,
            string diagnosticMessage)
        {
            var interruptedSolution = CreateCombinationFailure(
                algorithmKind,
                Enumerable.Empty<LinkageRouteCandidate>(),
                null,
                0,
                0,
                0d,
                diagnosticMessage);
            interruptedSolution.IsCancelled = true;
            interruptedSolution.Diagnostics[0].Stage =
                LinkageRouteFailureStage.Cancelled;
            return interruptedSolution;
        }

        private static bool IsBetterFailure(
            LinkageRouteSolution candidateFailure,
            LinkageRouteSolution currentFailure)
        {
            if (currentFailure == null)
            {
                return true;
            }

            var candidateDiagnostic = candidateFailure.Diagnostics.FirstOrDefault();
            var currentDiagnostic = currentFailure.Diagnostics.FirstOrDefault();
            if ((candidateDiagnostic?.CompletedGroupCount ?? 0) !=
                (currentDiagnostic?.CompletedGroupCount ?? 0))
            {
                return (candidateDiagnostic?.CompletedGroupCount ?? 0) >
                    (currentDiagnostic?.CompletedGroupCount ?? 0);
            }

            if ((candidateDiagnostic?.ViolationCount ?? int.MaxValue) !=
                (currentDiagnostic?.ViolationCount ?? int.MaxValue))
            {
                return (candidateDiagnostic?.ViolationCount ?? int.MaxValue) <
                    (currentDiagnostic?.ViolationCount ?? int.MaxValue);
            }

            return (candidateDiagnostic?.MaximumViolationDepth ?? double.PositiveInfinity) <
                (currentDiagnostic?.MaximumViolationDepth ?? double.PositiveInfinity);
        }

        private static IEnumerable<LinkageRouteCandidate> OrderCandidates(
            IEnumerable<LinkageRouteCandidate> routeCandidates,
            LinkageRouteAlgorithmKind algorithmKind,
            int beamWidth)
        {
            var orderedCandidates = routeCandidates
                .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                .ThenBy(candidate => candidate.SearchPriority)
                .ThenByDescending(candidate => candidate.MinimumStaticClearance)
                .ThenBy(candidate => LinkageRouteCandidateFactory
                    .GetCandidateSignature(candidate), StringComparer.Ordinal);
            return algorithmKind == LinkageRouteAlgorithmKind.TopologyBeam
                ? orderedCandidates.Take(Math.Max(1, beamWidth))
                : orderedCandidates;
        }

        private static string GetCandidateSetSignature(
            IEnumerable<LinkageRouteCandidate> routeCandidates)
        {
            return string.Join("||", routeCandidates
                .Select(LinkageRouteCandidateFactory.GetCandidateSignature)
                .OrderBy(candidateSignature => candidateSignature, StringComparer.Ordinal));
        }

        private static CandidatePairCacheKey GetPairCacheKey(
            LinkageRouteCandidate firstCandidate,
            LinkageRouteCandidate secondCandidate,
            double requiredClearance)
        {
            return new CandidatePairCacheKey(
                CandidateCacheKey.Create(firstCandidate),
                CandidateCacheKey.Create(secondCandidate),
                requiredClearance);
        }

        // 实例身份 + 签名版本与原“签名#INSTANCE=身份”字符串键一一对应：
        // 同一实例签名改变必然换键，不同实例永不共用。热路径不再拼接长签名。
        private struct CandidateCacheKey : IEquatable<CandidateCacheKey>
        {
            private CandidateCacheKey(long identity, long signatureVersion)
            {
                Identity = identity;
                SignatureVersion = signatureVersion;
            }

            public long Identity { get; }
            public long SignatureVersion { get; }

            public static CandidateCacheKey Create(LinkageRouteCandidate routeCandidate)
            {
                return new CandidateCacheKey(
                    routeCandidate.CacheIdentity,
                    routeCandidate.SignatureVersion);
            }

            public int CompareTo(CandidateCacheKey other)
            {
                var identityComparison = Identity.CompareTo(other.Identity);
                return identityComparison != 0
                    ? identityComparison
                    : SignatureVersion.CompareTo(other.SignatureVersion);
            }

            public bool Equals(CandidateCacheKey other)
            {
                return Identity == other.Identity &&
                    SignatureVersion == other.SignatureVersion;
            }

            public override bool Equals(object obj)
            {
                return obj is CandidateCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return unchecked((Identity.GetHashCode() * 397) ^
                    SignatureVersion.GetHashCode());
            }
        }

        // 与顺序无关的候选对键；净距按原 F6 文本精度归一，保持原缓存命中口径。
        private struct CandidatePairCacheKey : IEquatable<CandidatePairCacheKey>
        {
            public CandidatePairCacheKey(
                CandidateCacheKey firstCandidateKey,
                CandidateCacheKey secondCandidateKey,
                double requiredClearance)
            {
                if (firstCandidateKey.CompareTo(secondCandidateKey) > 0)
                {
                    var swappedCandidateKey = firstCandidateKey;
                    firstCandidateKey = secondCandidateKey;
                    secondCandidateKey = swappedCandidateKey;
                }

                FirstCandidateKey = firstCandidateKey;
                SecondCandidateKey = secondCandidateKey;
                RequiredClearance = Math.Round(
                    requiredClearance,
                    6,
                    MidpointRounding.AwayFromZero);
            }

            public CandidateCacheKey FirstCandidateKey { get; }
            public CandidateCacheKey SecondCandidateKey { get; }
            public double RequiredClearance { get; }

            public bool Equals(CandidatePairCacheKey other)
            {
                return FirstCandidateKey.Equals(other.FirstCandidateKey) &&
                    SecondCandidateKey.Equals(other.SecondCandidateKey) &&
                    RequiredClearance.Equals(other.RequiredClearance);
            }

            public override bool Equals(object obj)
            {
                return obj is CandidatePairCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = FirstCandidateKey.GetHashCode();
                    hashCode = (hashCode * 397) ^ SecondCandidateKey.GetHashCode();
                    return (hashCode * 397) ^ RequiredClearance.GetHashCode();
                }
            }
        }

        private sealed class SearchContext
        {
            private readonly object resultGate = new object();
            private readonly object outputPublicationGate = new object();
            private readonly object backgroundTaskLifecycleGate = new object();
            private readonly ConcurrentDictionary<CandidatePairCacheKey,
                PairClearanceEvaluation> pairClearanceCache =
                    new ConcurrentDictionary<CandidatePairCacheKey,
                        PairClearanceEvaluation>();
            private readonly ConcurrentDictionary<CandidatePairCacheKey, double>
                exactPairDistanceCache =
                    new ConcurrentDictionary<CandidatePairCacheKey, double>();
            private readonly ConcurrentDictionary<CandidateCacheKey, double>
                exactStaticDistanceCache =
                    new ConcurrentDictionary<CandidateCacheKey, double>();
            private readonly ConcurrentDictionary<string,
                Lazy<IReadOnlyList<LinkageRouteCandidate>>> clearanceExpansionCache =
                    new ConcurrentDictionary<string,
                        Lazy<IReadOnlyList<LinkageRouteCandidate>>>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string,
                IReadOnlyList<LinkageRouteCandidate>> pairViolationRepairCache =
                    new ConcurrentDictionary<string,
                        IReadOnlyList<LinkageRouteCandidate>>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, byte> visitedSearchStates =
                new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<LinkageRouteCandidatePool,
                IReadOnlyList<LinkageRouteCandidate>> orderedCandidatePoolCache =
                    new ConcurrentDictionary<LinkageRouteCandidatePool,
                        IReadOnlyList<LinkageRouteCandidate>>();
            private readonly Dictionary<string, LinkageRouteSolution>
                successfulSearchSolutions =
                    new Dictionary<string, LinkageRouteSolution>(
                        StringComparer.Ordinal);
            private readonly Dictionary<string, LinkageRouteSolution> successfulSolutions =
                new Dictionary<string, LinkageRouteSolution>(StringComparer.Ordinal);
            private readonly Dictionary<string, LinkageRouteSolution>
                successfulOutputSourceSearchSolutions =
                    new Dictionary<string, LinkageRouteSolution>(
                        StringComparer.Ordinal);
            private readonly Dictionary<string,
                IReadOnlyList<LinkageRouteCandidate>> successfulCandidateSets =
                    new Dictionary<string, IReadOnlyList<LinkageRouteCandidate>>(
                        StringComparer.Ordinal);
            private readonly Dictionary<string,
                IReadOnlyList<LinkageRouteCandidate>> publishedCandidateSets =
                    new Dictionary<string, IReadOnlyList<LinkageRouteCandidate>>(
                        StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, byte>
                publishedCompleteTopologySignatures =
                    new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string,
                ConcurrentDictionary<string, LinkageRouteCandidate>>
                solutionOptimizationCandidatesByGroup =
                    new ConcurrentDictionary<string,
                        ConcurrentDictionary<string, LinkageRouteCandidate>>(
                            StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<string, byte>
                scheduledSolutionOptimizationSignatures =
                    new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string,
                ConcurrentDictionary<string, CancellationTokenSource>>
                activeSolutionOptimizationCancellationSources =
                    new ConcurrentDictionary<string,
                        ConcurrentDictionary<string, CancellationTokenSource>>(
                            StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string,
                Lazy<IReadOnlyList<LinkageRouteCandidate>>>
                solutionOptimizationCandidateCache =
                    new ConcurrentDictionary<string,
                        Lazy<IReadOnlyList<LinkageRouteCandidate>>>(
                            StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, long>
                solutionOptimizationLineageStartMilliseconds =
                    new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            private readonly SemaphoreSlim solutionOptimizationSemaphore;
            private readonly SemaphoreSlim deepSolutionOptimizationSemaphore;
            private readonly SemaphoreSlim mainSearchWorkerSemaphore;
            private readonly SemaphoreSlim solutionAllocationUpdateSemaphore =
                new SemaphoreSlim(1, 1);
            // 后处理只在深拷贝上计算，结果提交仍经同一串行发布通道；
            // 允许有限并发，避免单条失败恢复把整条输出队列串成长链。
            private readonly SemaphoreSlim terminalOutputPostProcessingSemaphore;
            private readonly SemaphoreSlim terminalOutputQueueSlots;
            private readonly LinkageRouteSearchRuntimeControl runtimeControl;
            private readonly CancellationTokenSource mainSearchCancellationSource;
            private readonly ManualResetEventSlim solutionOptimizationIdle =
                new ManualResetEventSlim(true);
            private readonly ManualResetEventSlim
                terminalOutputPostProcessingIdle =
                    new ManualResetEventSlim(true);
            private IReadOnlyList<LinkageRouteCandidatePool>
                solutionOptimizationCandidatePools =
                    Array.Empty<LinkageRouteCandidatePool>();
            private int pendingSolutionOptimizationCount;
            private int pendingTerminalOutputPostProcessingCount;
            private int pendingBackgroundTaskCount;
            private int acceptsSolutionOptimizationTasks = 1;
            private int acceptsTerminalOutputPostProcessingTasks = 1;
            private int initialSolutionOptimizationAllocationActivated;
            private int postSwitchSolutionAllocationActivated;
            private int stagnationSolutionAllocationActivated;
            private int optimizationPriorityAllocationActivated;
            private int exhaustedMainSearchAllocationActivated;
            private readonly bool usesRouteFamilyScheduling;
            private int explorationWindowEndScheduled;
            private int runtimeControlDetached;
            // 路径族 → 已发布谱系键（见 LinkageRouteTopologySignature.CreateRouteFamilySignature）。
            private readonly ConcurrentDictionary<string, string> publishedRouteFamilyLineages =
                new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, double> publishedRouteFamilyBestLengths =
                new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            private readonly Dictionary<string, double> routeFamilyJogThresholds =
                new Dictionary<string, double>(StringComparer.Ordinal);
            private int currentRetainedMainSearchWorkerCount;
            private int currentSolutionOptimizationWorkerCount;
            private int currentDeepSolutionOptimizationWorkerCount;
            private long solutionOptimizationCatalogVersion;
            private long lastRetainedSolutionElapsedMilliseconds = -1L;
            private LinkageRouteSolution bestFailureSolution;
            private string firstFinalOutputRejectionReason;
            private double worstRetainedSearchSolutionLength =
                double.PositiveInfinity;
            private int worstRetainedSearchSolutionPartCount = int.MaxValue;
            private int retainedSearchSolutionCount;

            public SearchContext(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageMultiGroupRouteRequest outputRouteRequest,
                LinkageRouteAlgorithmKind algorithmKind,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IProgress<LinkageRouteProgress> progress,
                Stopwatch searchStopwatch,
                CancellationToken cancellationToken)
            {
                RouteRequest = routeRequest;
                OutputRouteRequest = outputRouteRequest;
                AlgorithmKind = algorithmKind;
                FixedTerminalAndObstacleSolids = fixedTerminalAndObstacleSolids;
                Progress = progress;
                SearchStopwatch = searchStopwatch;
                CancellationToken = cancellationToken;
                runtimeControl = routeRequest.RuntimeControl;
                mainSearchCancellationSource = runtimeControl == null
                    ? CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        runtimeControl.NewSolutionSearchCancellationToken);
                var availableParallelWorkerCount =
                    ResolveMaximumDegreeOfParallelism(routeRequest);
                terminalOutputQueueSlots = new SemaphoreSlim(
                    availableParallelWorkerCount, availableParallelWorkerCount);
                var terminalOutputPostProcessingConcurrency = Math.Max(
                    1, availableParallelWorkerCount / 3);
                terminalOutputPostProcessingSemaphore = new SemaphoreSlim(
                    terminalOutputPostProcessingConcurrency,
                    terminalOutputPostProcessingConcurrency);
                solutionOptimizationSemaphore = new SemaphoreSlim(
                    0,
                    availableParallelWorkerCount);
                deepSolutionOptimizationSemaphore = new SemaphoreSlim(
                    0,
                    availableParallelWorkerCount);
                mainSearchWorkerSemaphore = new SemaphoreSlim(
                    availableParallelWorkerCount,
                    availableParallelWorkerCount);
                currentRetainedMainSearchWorkerCount =
                    availableParallelWorkerCount;
                currentSolutionOptimizationWorkerCount = 0;
                currentDeepSolutionOptimizationWorkerCount = 0;
                // 异规格搜索（主分支与通用/包络辅助分支）的内部发布还要经外层实体化复核，
                // 被拒的内部方案不能拿来判重或改配线程，否则同族中可实体化的方案会被挡掉；
                // 这类搜索保持原有判重与分配规则。
                usesRouteFamilyScheduling = !routeRequest.IsSectionSpecificationAuxiliarySearch &&
                    !routeRequest.Groups.Any(routeGroupRequest =>
                        routeGroupRequest?.HasSectionSpecificBusbarSettings(routeRequest) == true);
                foreach (var routeGroupRequest in routeRequest.Groups)
                {
                    if (routeGroupRequest?.GroupKey == null ||
                        routeFamilyJogThresholds.ContainsKey(routeGroupRequest.GroupKey))
                        continue;
                    // 小于一个排距（排宽 + 绝缘距离）的行程绕不过相邻相排，只算偏移。
                    routeFamilyJogThresholds[routeGroupRequest.GroupKey] =
                        routeGroupRequest.GetBusbarWidth(routeRequest) +
                        routeRequest.InsulationClearance;
                }
                if (runtimeControl != null)
                {
                    runtimeControl.OptimizationPriorityRequested +=
                        ActivateOptimizationPriority;
                    if (runtimeControl.IsOptimizationPriorityRequested)
                    {
                        ActivateOptimizationPriority();
                    }
                }
            }

            public LinkageMultiGroupRouteRequest RouteRequest { get; }
            public LinkageMultiGroupRouteRequest OutputRouteRequest { get; }
            public LinkageRouteAlgorithmKind AlgorithmKind { get; }
            public IReadOnlyList<LinkageRouteSolid> FixedTerminalAndObstacleSolids { get; }
            public IProgress<LinkageRouteProgress> Progress { get; }
            public Stopwatch SearchStopwatch { get; }
            public CancellationToken CancellationToken { get; }
            public CancellationToken MainSearchCancellationToken =>
                mainSearchCancellationSource.Token;
            public long ProcessedStateCount;
            public long PairViolationRepairGenerationCount;
            public long PairViolationRepairCandidateCount;
            public string PairViolationFirstFailureDiagnostic;

            public bool HasSuccessfulSolution
            {
                get
                {
                    lock (resultGate)
                    {
                        // 待处理普通候选不是成功输出；最终门禁拒绝的候选
                        // 不能结束首方案搜索或触发后续搜索阶段。
                        return successfulSolutions.Count > 0;
                    }
                }
            }

            public void BeginContinuationStage()
            {
                // 时间片或候选层切换后必须清空访问状态；上一阶段可能在并行分支
                // 中途停止，复用其查重集合会把下一阶段尚未完整展开的状态误删。
                visitedSearchStates.Clear();
            }

            // 附加阶段（顺序避让重规划）前后保存/恢复原搜索会读取的查重与失败记录，
            // 使附加阶段除占用有界时间和发布已过完整门禁的方案外，不改变原搜索后续行为。
            public AuxiliaryStageStateSnapshot CaptureAuxiliaryStageState()
            {
                lock (resultGate)
                {
                    return new AuxiliaryStageStateSnapshot(visitedSearchStates.ToArray(),
                        bestFailureSolution, firstFinalOutputRejectionReason);
                }
            }

            public void RestoreAuxiliaryStageState(AuxiliaryStageStateSnapshot snapshot)
            {
                if (snapshot == null) return;
                visitedSearchStates.Clear();
                foreach (var entry in snapshot.VisitedSearchStates)
                    visitedSearchStates.TryAdd(entry.Key, entry.Value);
                lock (resultGate)
                {
                    bestFailureSolution = snapshot.BestFailureSolution;
                    firstFinalOutputRejectionReason = snapshot.FirstFinalOutputRejectionReason;
                }
            }

            public bool TryEnterMainSearchWorker(
                CancellationToken searchStageCancellationToken)
            {
                TryActivateStagnationSolutionAllocation();
                try
                {
                    mainSearchWorkerSemaphore.Wait(
                        searchStageCancellationToken);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            public void ExitMainSearchWorker()
            {
                mainSearchWorkerSemaphore.Release();
            }

            private void TryActivateInitialSolutionOptimizationAllocation()
            {
                if (!HasSuccessfulSolution)
                    return;
                if (Interlocked.CompareExchange(
                        ref initialSolutionOptimizationAllocationActivated,
                        1,
                        0) != 0)
                {
                    return;
                }

                ScheduleSolutionAllocationUpdate();
            }

            private bool IsExplorationWindowActive =>
                usesRouteFamilyScheduling &&
                Volatile.Read(ref optimizationPriorityAllocationActivated) == 0 &&
                SearchStopwatch.ElapsedMilliseconds < ExplorationWindowMilliseconds;

            /// <summary>
            /// 主搜索全部阶段正常结束（未被时限或取消中断）后调用：所有路径已找完，
            /// 全部线程转去优化已发布方案（复用“优先优化”分配）。没有成功方案时不动作。
            /// </summary>
            public void ActivateExhaustedMainSearchOptimization()
            {
                if (!usesRouteFamilyScheduling ||
                    CancellationToken.IsCancellationRequested || !HasSuccessfulSolution ||
                    Volatile.Read(ref acceptsSolutionOptimizationTasks) == 0)
                    return;
                if (Interlocked.CompareExchange(ref exhaustedMainSearchAllocationActivated, 1, 0) != 0)
                    return;
                ActivateOptimizationPriority();
            }

            // 探索期结束时恢复原分配；期内跳过的已发布方案按原规则补排优化。
            private void ScheduleExplorationWindowEnd()
            {
                if (Interlocked.CompareExchange(ref explorationWindowEndScheduled, 1, 0) != 0)
                    return;
                var remainingMilliseconds = ExplorationWindowMilliseconds -
                    SearchStopwatch.ElapsedMilliseconds;
                Task.Run(async () =>
                {
                    try
                    {
                        if (remainingMilliseconds > 0L)
                            await Task.Delay(TimeSpan.FromMilliseconds(remainingMilliseconds + 50L),
                                CancellationToken).ConfigureAwait(false);
                        if (CancellationToken.IsCancellationRequested ||
                            Volatile.Read(ref acceptsSolutionOptimizationTasks) == 0 ||
                            Volatile.Read(ref optimizationPriorityAllocationActivated) != 0)
                            return;
                        ScheduleSolutionAllocationUpdate();
                        if (Volatile.Read(ref postSwitchSolutionAllocationActivated) != 0)
                            RefreshPreferredSolutionOptimizations();
                        else
                            SchedulePublishedSolutionReoptimizations();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }

            private void ActivateOptimizationPriority()
            {
                if (Interlocked.CompareExchange(
                        ref optimizationPriorityAllocationActivated,
                        1,
                        0) != 0)
                {
                    return;
                }

                Interlocked.Exchange(
                    ref postSwitchSolutionAllocationActivated,
                    1);
                ScheduleSolutionAllocationUpdate();
                RefreshPreferredSolutionOptimizations();
            }

            private void TryActivatePostSwitchSolutionAllocation()
            {
                lock (resultGate)
                {
                    if (successfulSolutions.Count < RouteRequest.SearchOptions
                            .SolutionAllocationSwitchSolutionCount)
                        return;
                }
                if (Interlocked.CompareExchange(
                        ref postSwitchSolutionAllocationActivated,
                        1,
                        0) != 0)
                {
                    return;
                }

                ScheduleSolutionAllocationUpdate();
            }

            private void TryActivateStagnationSolutionAllocation()
            {
                if (Volatile.Read(
                        ref optimizationPriorityAllocationActivated) != 0)
                {
                    return;
                }

                if (Volatile.Read(
                        ref stagnationSolutionAllocationActivated) != 0)
                {
                    return;
                }

                var lastRetainedSolutionMilliseconds = Interlocked.Read(
                    ref lastRetainedSolutionElapsedMilliseconds);
                if (lastRetainedSolutionMilliseconds < 0L ||
                    SearchStopwatch.ElapsedMilliseconds -
                        lastRetainedSolutionMilliseconds < RouteRequest
                            .SearchOptions
                            .SolutionOptimizationStagnationMilliseconds)
                {
                    return;
                }

                if (Interlocked.CompareExchange(
                        ref stagnationSolutionAllocationActivated,
                        1,
                        0) != 0)
                {
                    return;
                }

                Interlocked.Exchange(
                    ref postSwitchSolutionAllocationActivated,
                    1);
                ScheduleSolutionAllocationUpdate();
                RefreshPreferredSolutionOptimizations();
            }

            private void ScheduleSolutionAllocationUpdate()
            {
                Task.Run(async () =>
                {
                    var enteredAllocationUpdateSemaphore = false;
                    try
                    {
                        await solutionAllocationUpdateSemaphore
                            .WaitAsync(CancellationToken)
                            .ConfigureAwait(false);
                        enteredAllocationUpdateSemaphore = true;

                        var stagnationAllocationActivated = Volatile.Read(
                            ref stagnationSolutionAllocationActivated) != 0;
                        var postSwitchAllocationActivated = Volatile.Read(
                            ref postSwitchSolutionAllocationActivated) != 0;
                        var optimizationPriorityAllocationActivated = Volatile.Read(
                            ref this.optimizationPriorityAllocationActivated) != 0;
                        var availableParallelWorkerCount =
                            ResolveMaximumDegreeOfParallelism(RouteRequest);
                        var explorationWindowActive =
                            !optimizationPriorityAllocationActivated &&
                            IsExplorationWindowActive;
                        if (explorationWindowActive)
                        {
                            // 探索期沿用首方案分配（主搜索 = 可用 − 优化路数，优化至多 2 路），
                            // 忽略“≥N 个方案”切换；期满由定时任务按原规则重新分配。分配只会
                            // 单调收缩主搜索，探索期数值与首方案分配相同，不会出现回增。
                            stagnationAllocationActivated = false;
                            postSwitchAllocationActivated = false;
                            ScheduleExplorationWindowEnd();
                        }
                        var retainedMainSearchWorkerCount =
                            optimizationPriorityAllocationActivated
                                ? 0
                                : stagnationAllocationActivated
                                ? ResolveStagnationMainSearchWorkerCount(
                                    RouteRequest)
                                : postSwitchAllocationActivated
                                    ? ResolvePostSwitchMainSearchWorkerCount(
                                        RouteRequest)
                                    : ResolveInitialMainSearchWorkerCount(
                                        RouteRequest);
                        var solutionOptimizationWorkerCount =
                            optimizationPriorityAllocationActivated
                                ? availableParallelWorkerCount
                                : stagnationAllocationActivated
                                ? ResolveStagnationSolutionOptimizationWorkerCount(
                                    RouteRequest)
                                : postSwitchAllocationActivated
                                    ? ResolvePostSwitchSolutionOptimizationWorkerCount(
                                        RouteRequest)
                                    : ResolveSolutionOptimizationWorkerCount(
                                        RouteRequest);
                        var deepSolutionOptimizationWorkerCount =
                            optimizationPriorityAllocationActivated
                                ? Math.Max(
                                    1,
                                    availableParallelWorkerCount - RouteRequest
                                        .SearchOptions
                                        .FastSolutionOptimizationWorkerReserve)
                                : stagnationAllocationActivated
                                ? ResolveStagnationDeepSolutionOptimizationWorkerCount(
                                    RouteRequest)
                                : postSwitchAllocationActivated
                                    ? ResolvePostSwitchDeepSolutionOptimizationWorkerCount(
                                        RouteRequest)
                                    : ResolveDeepSolutionOptimizationWorkerCount(
                                        RouteRequest);

                        var additionalReservedMainSearchWorkerCount = Math.Max(
                            0,
                            Volatile.Read(
                                ref currentRetainedMainSearchWorkerCount) -
                                retainedMainSearchWorkerCount);
                        for (var reservedWorkerIndex = 0;
                            reservedWorkerIndex <
                                additionalReservedMainSearchWorkerCount;
                            reservedWorkerIndex++)
                        {
                            await mainSearchWorkerSemaphore
                                .WaitAsync(CancellationToken)
                                .ConfigureAwait(false);
                        }

                        var additionalOptimizationWorkerCount = Math.Max(
                            0,
                            solutionOptimizationWorkerCount -
                                Volatile.Read(
                                    ref currentSolutionOptimizationWorkerCount));
                        if (additionalOptimizationWorkerCount > 0)
                        {
                            solutionOptimizationSemaphore.Release(
                                additionalOptimizationWorkerCount);
                        }

                        var additionalDeepOptimizationWorkerCount = Math.Max(
                            0,
                            deepSolutionOptimizationWorkerCount -
                                Volatile.Read(
                                    ref currentDeepSolutionOptimizationWorkerCount));
                        if (additionalDeepOptimizationWorkerCount > 0)
                        {
                            deepSolutionOptimizationSemaphore.Release(
                                additionalDeepOptimizationWorkerCount);
                        }

                        Volatile.Write(
                            ref currentRetainedMainSearchWorkerCount,
                            retainedMainSearchWorkerCount);
                        Volatile.Write(
                            ref currentSolutionOptimizationWorkerCount,
                            solutionOptimizationWorkerCount);
                        Volatile.Write(
                            ref currentDeepSolutionOptimizationWorkerCount,
                            deepSolutionOptimizationWorkerCount);

                        Progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = AlgorithmKind,
                            ElapsedMilliseconds =
                                SearchStopwatch.ElapsedMilliseconds,
                            ProcessedStateCount = Interlocked.Read(
                                ref ProcessedStateCount),
                            Message = optimizationPriorityAllocationActivated
                                ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    Volatile.Read(ref exhaustedMainSearchAllocationActivated) != 0
                                        ? "全部路径已搜索完毕，{0}/{0} 路全部用于优化最终验证通过的前 {1} 套方案；" +
                                          "更短或折弯更少的结果会继续进入下一轮优化。"
                                        : "已切换为优先优化方案：停止全部新方案搜索，" +
                                          "{0}/{0} 路用于持续优化最终验证通过的前 {1} 套方案；" +
                                          "更短结果会继续进入下一轮优化。",
                                    availableParallelWorkerCount,
                                    PrioritizedOptimizationSolutionCount)
                                : explorationWindowActive
                                ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    "探索期（前 {0:F0} 秒）：主搜索 {1}/{2} 路全力寻找不同路径，同一路径族不重复发布；" +
                                    "后台 {3}/{2} 路只优化当前最短的 {4} 个方案（含 4 段及以上排的减折弯段）；" +
                                    "期满或路径找完后按原规则分配。",
                                    ExplorationWindowMilliseconds / 1000d,
                                    retainedMainSearchWorkerCount,
                                    availableParallelWorkerCount,
                                    solutionOptimizationWorkerCount,
                                    ExplorationOptimizedSolutionCount)
                                : stagnationAllocationActivated
                                ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    "连续 {0:F0} 秒没有保留更优或新方案：主搜索只保留 {1}/{2} 路，" +
                                    "其余 {3}/{2} 路持续优化优化方案前 {4} 个和未优化方案前 {4} 个；另保留 {5} 路用于任务调度和结果发布。",
                                    RouteRequest.SearchOptions
                                        .SolutionOptimizationStagnationMilliseconds /
                                        1000d,
                                    retainedMainSearchWorkerCount,
                                    availableParallelWorkerCount,
                                    solutionOptimizationWorkerCount,
                                    RouteRequest.SearchOptions
                                        .PreferredSolutionOptimizationCount,
                                    RouteRequest.SearchOptions
                                        .SchedulingAndPublishingWorkerReserve)
                                : postSwitchAllocationActivated
                                    ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    "已获得 {0} 个方案：主搜索保留 {1}/{2} 路，" +
                                    "后台方案优化使用 {3}/{2} 路，动态优先优化方案前 {4} 个和未优化方案前 {4} 个；" +
                                    "另保留 {5} 路用于任务调度和结果发布。",
                                    RouteRequest.SearchOptions
                                        .SolutionAllocationSwitchSolutionCount,
                                    retainedMainSearchWorkerCount,
                                    availableParallelWorkerCount,
                                    solutionOptimizationWorkerCount,
                                    RouteRequest.SearchOptions
                                        .PreferredSolutionOptimizationCount,
                                    RouteRequest.SearchOptions
                                        .SchedulingAndPublishingWorkerReserve)
                                    : string.Format(
                                        CultureInfo.InvariantCulture,
                                        "首个方案已保留：主搜索保留 {0}/{1} 路，" +
                                        "后台方案优化使用 {2}/{1} 路；总并发不超过可用预算，" +
                                        "另保留 {3} 路用于任务调度和结果发布。",
                                        retainedMainSearchWorkerCount,
                                        availableParallelWorkerCount,
                                        solutionOptimizationWorkerCount,
                                        RouteRequest.SearchOptions
                                            .SchedulingAndPublishingWorkerReserve)
                        });
                    }
                    catch (OperationCanceledException) when (
                        CancellationToken.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        if (enteredAllocationUpdateSemaphore)
                        {
                            solutionAllocationUpdateSemaphore.Release();
                        }
                    }
                });
            }

            public IReadOnlyList<LinkageRouteCandidate> GetOrderedCandidates(
                LinkageRouteCandidatePool candidatePool)
            {
                if (candidatePool == null)
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                return orderedCandidatePoolCache.GetOrAdd(
                    candidatePool,
                    uncachedCandidatePool => OrderCandidates(
                            uncachedCandidatePool.ValidCandidates,
                            AlgorithmKind,
                            RouteRequest.SearchOptions.BeamWidth)
                        .ToList());
            }

            public void SetSolutionOptimizationCandidatePools(
                IEnumerable<LinkageRouteCandidatePool> candidatePools)
            {
                var candidatePoolSnapshot = (candidatePools ??
                        Enumerable.Empty<LinkageRouteCandidatePool>())
                    .Where(candidatePool => candidatePool != null)
                    .ToList();
                Volatile.Write(
                    ref solutionOptimizationCandidatePools,
                    candidatePoolSnapshot);
                if (RegisterSolutionOptimizationCandidatePools(
                    candidatePoolSnapshot))
                {
                    SchedulePublishedSolutionReoptimizations();
                }
            }

            private bool RegisterSolutionOptimizationCandidatePools(
                IEnumerable<LinkageRouteCandidatePool> candidatePools)
            {
                var registeredNewCandidate = false;
                foreach (var candidatePool in candidatePools ??
                    Enumerable.Empty<LinkageRouteCandidatePool>())
                {
                    foreach (var routeCandidate in candidatePool?.ValidCandidates ??
                        new List<LinkageRouteCandidate>())
                    {
                        registeredNewCandidate |= RegisterSolutionOptimizationCandidate(
                            routeCandidate);
                    }
                }

                return registeredNewCandidate;
            }

            private void SchedulePublishedSolutionReoptimizations()
            {
                List<KeyValuePair<string, IReadOnlyList<LinkageRouteCandidate>>>
                    publishedCandidateSetSnapshot;
                lock (resultGate)
                {
                    var candidateSetPairs = publishedCandidateSets.AsEnumerable();
                    if (UsesPreferredSolutionOptimizationSet)
                    {
                        var preferredTopologySignatures =
                            GetPreferredSolutionOptimizationTopologySignaturesLocked();
                        candidateSetPairs = candidateSetPairs.Where(
                            candidateSetPair => preferredTopologySignatures.Contains(
                                candidateSetPair.Key));
                    }

                    publishedCandidateSetSnapshot = candidateSetPairs.Select(
                            candidateSetPair => new KeyValuePair<string,
                                IReadOnlyList<LinkageRouteCandidate>>(
                                candidateSetPair.Key,
                                candidateSetPair.Value.ToList()))
                        .ToList();
                }

                if (publishedCandidateSetSnapshot.Count == 0)
                {
                    return;
                }

                var candidateCatalogVersion = Interlocked.Increment(
                    ref solutionOptimizationCatalogVersion);
                foreach (var publishedCandidateSet in
                    publishedCandidateSetSnapshot)
                {
                    ScheduleSolutionOptimization(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CATALOG={0}||{1}",
                            candidateCatalogVersion,
                            GetCandidateSetSignature(publishedCandidateSet.Value)),
                        publishedCandidateSet.Key,
                        publishedCandidateSet.Value);
                }
            }

            /// <summary>
            /// 等待已登记的柜顶/端部输出后处理全部结束（不关闭任务入口）。顺序避让阶段用它区分
            /// “完整门禁仍在异步后处理中”和“已被拒绝”，避免把待处理当成失败继续下一轮。
            /// </summary>
            public void WaitTerminalOutputPostProcessingIdle(CancellationToken waitCancellationToken)
            {
                try
                {
                    terminalOutputPostProcessingIdle.Wait(waitCancellationToken);
                }
                catch (OperationCanceledException) when (waitCancellationToken.IsCancellationRequested)
                {
                }
            }

            public void CompletePendingSolutionOptimizations()
            {
                // 输出与优化会互相登记子任务；父任务退出之前子任务已计入
                // 同一总数。检查排空与关闭入口必须和登记共用同一把锁。
                lock (backgroundTaskLifecycleGate)
                {
                    while (pendingBackgroundTaskCount > 0)
                        Monitor.Wait(backgroundTaskLifecycleGate, 50);
                    Volatile.Write(ref acceptsSolutionOptimizationTasks, 0);
                    Volatile.Write(ref acceptsTerminalOutputPostProcessingTasks, 0);
                }

                if (Interlocked.Exchange(ref runtimeControlDetached, 1) == 0)
                {
                    if (runtimeControl != null)
                    {
                        runtimeControl.OptimizationPriorityRequested -=
                            ActivateOptimizationPriority;
                    }

                    mainSearchCancellationSource.Dispose();
                }
            }

            private bool TryRegisterBackgroundTask(bool isTerminalOutput)
            {
                lock (backgroundTaskLifecycleGate)
                {
                    if (CancellationToken.IsCancellationRequested ||
                        (isTerminalOutput ? acceptsTerminalOutputPostProcessingTasks : acceptsSolutionOptimizationTasks) == 0)
                        return false;
                    pendingBackgroundTaskCount++;
                    if (isTerminalOutput)
                    {
                        pendingTerminalOutputPostProcessingCount++;
                        terminalOutputPostProcessingIdle.Reset();
                    }
                    else
                    {
                        pendingSolutionOptimizationCount++;
                        solutionOptimizationIdle.Reset();
                    }
                    return true;
                }
            }

            private void FinishBackgroundTask(bool isTerminalOutput)
            {
                lock (backgroundTaskLifecycleGate)
                {
                    if (isTerminalOutput)
                    {
                        if (--pendingTerminalOutputPostProcessingCount == 0)
                            terminalOutputPostProcessingIdle.Set();
                    }
                    else if (--pendingSolutionOptimizationCount == 0)
                        solutionOptimizationIdle.Set();
                    pendingBackgroundTaskCount--;
                    Monitor.PulseAll(backgroundTaskLifecycleGate);
                }
            }

            public bool TryVisitState(IEnumerable<LinkageRouteCandidate> selectedCandidates)
            {
                return visitedSearchStates.TryAdd(
                    GetCandidateSetSignature(selectedCandidates),
                    0);
            }

            public bool HasPublishedCompleteTopology(
                IEnumerable<LinkageRouteCandidate> completeCandidateSet)
            {
                if (publishedCompleteTopologySignatures.IsEmpty)
                {
                    return false;
                }

                var completeCandidateGroupCenterlines =
                    CreateCandidateGroupCenterlines(completeCandidateSet);
                var completeCandidateTopologySignature =
                    LinkageRouteTopologySignature.CreateSolutionSignature(
                        completeCandidateGroupCenterlines);
                if (publishedCompleteTopologySignatures.ContainsKey(
                        completeCandidateTopologySignature))
                    return true;
                // 同族已发布：只有比该族当前代表更短时才继续验证（作为该族代表的替换发布）。
                return TryGetPublishedRouteFamily(completeCandidateGroupCenterlines,
                        out _, out var familyBestLength) &&
                    completeCandidateSet.Sum(routeCandidate =>
                        routeCandidate.PlanarConversion.ActualCopperLength) + 1e-8d >=
                        familyBestLength;
            }

            private double GetRouteFamilyJogThreshold(string groupKey)
            {
                return groupKey != null &&
                    routeFamilyJogThresholds.TryGetValue(groupKey, out var jogThreshold)
                        ? jogThreshold
                        : RouteRequest.InsulationClearance;
            }

            private string CreateRouteFamilySignature(
                IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>> groupCenterlines)
            {
                return LinkageRouteTopologySignature.CreateRouteFamilySignature(
                    groupCenterlines, GetRouteFamilyJogThreshold);
            }

            private string CreateRouteFamilySignature(LinkageRouteSolution routeSolution)
            {
                return LinkageRouteTopologySignature.CreateRouteFamilySignature(
                    routeSolution, GetRouteFamilyJogThreshold);
            }

            // 同一路径族已发布时，主搜索不再另起新方案；更短的同族方案作为该族谱系的替换发布。
            // 只在已有完整门禁通过的方案后才会命中，不影响“无解→有解”。
            private bool TryGetPublishedRouteFamily(
                IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>> groupCenterlines,
                out string familyLineage,
                out double familyBestLength)
            {
                familyLineage = null;
                familyBestLength = double.PositiveInfinity;
                if (!usesRouteFamilyScheduling || publishedRouteFamilyLineages.IsEmpty)
                    return false;
                var familySignature = CreateRouteFamilySignature(groupCenterlines);
                if (!publishedRouteFamilyLineages.TryGetValue(familySignature, out familyLineage))
                    return false;
                if (!publishedRouteFamilyBestLengths.TryGetValue(familySignature, out familyBestLength))
                    familyBestLength = double.PositiveInfinity;
                return true;
            }

            public PairClearanceEvaluation EvaluateAgainstSelected(
                LinkageRouteCandidate routeCandidate,
                IEnumerable<LinkageRouteCandidate> selectedCandidates)
            {
                return EvaluateAgainstSelected(
                    routeCandidate,
                    selectedCandidates,
                    CancellationToken);
            }

            public PairClearanceEvaluation EvaluateAgainstSelected(
                LinkageRouteCandidate routeCandidate,
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                CancellationToken evaluationCancellationToken)
            {
                var combinedEvaluation = new PairClearanceEvaluation
                {
                    IsClear = true,
                    MinimumClearance = routeCandidate.MinimumStaticClearance
                };
                foreach (var selectedCandidate in selectedCandidates)
                {
                    if (evaluationCancellationToken.IsCancellationRequested)
                    {
                        combinedEvaluation.IsClear = false;
                        return combinedEvaluation;
                    }

                    var requiredCandidatePairInsulationClearance =
                        GetRequiredCandidatePairInsulationClearance(
                            routeCandidate,
                            selectedCandidate);
                    var pairCacheKey = GetPairCacheKey(
                        routeCandidate,
                        selectedCandidate,
                        requiredCandidatePairInsulationClearance);
                    if (!pairClearanceCache.TryGetValue(
                            pairCacheKey,
                            out var pairEvaluation))
                    {
                        pairEvaluation = EvaluateCandidatePair(
                            routeCandidate,
                            selectedCandidate,
                            requiredCandidatePairInsulationClearance,
                            evaluationCancellationToken);
                        // 时间片取消得到的是未完成结果，不能写入跨阶段缓存；否则
                        // 后续完整搜索会把“未算完”误当成真实的绝缘失败。
                        if (!evaluationCancellationToken.IsCancellationRequested)
                        {
                            pairClearanceCache.TryAdd(pairCacheKey, pairEvaluation);
                        }
                    }
                    combinedEvaluation.IsClear &= pairEvaluation.IsClear;
                    combinedEvaluation.MinimumClearance = Math.Min(
                        combinedEvaluation.MinimumClearance,
                        pairEvaluation.MinimumClearance);
                    combinedEvaluation.ViolationCount += pairEvaluation.ViolationCount;
                    combinedEvaluation.GeneralClearanceViolationCount += pairEvaluation.GeneralClearanceViolationCount;
                    combinedEvaluation.LapClearanceViolationCount += pairEvaluation.LapClearanceViolationCount;
                    combinedEvaluation.SameGroupOverlapCount += pairEvaluation.SameGroupOverlapCount;
                    if (pairEvaluation.MaximumViolationDepth >
                        combinedEvaluation.MaximumViolationDepth)
                    {
                        combinedEvaluation.MaximumViolationDepth =
                            pairEvaluation.MaximumViolationDepth;
                        combinedEvaluation.CandidateSegmentIndex =
                            pairEvaluation.CandidateSegmentIndex;
                        combinedEvaluation.SelectedSegmentIndex =
                            pairEvaluation.SelectedSegmentIndex;
                        combinedEvaluation.ConflictingGroupKey =
                            pairEvaluation.ConflictingGroupKey;
                        combinedEvaluation.RequiredClearance =
                            pairEvaluation.RequiredClearance;
                        combinedEvaluation.SuggestedSeparationDistance =
                            pairEvaluation.SuggestedSeparationDistance;
                        combinedEvaluation.SuggestedSeparationDirection =
                            pairEvaluation.SuggestedSeparationDirection;
                    }
                }

                return combinedEvaluation;
            }

            public IReadOnlyList<LinkageRouteCandidate> GetClearanceExpansionCandidates(
                LinkageRouteCandidatePool candidatePool,
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                bool allowDeepClearanceExpansionCandidates,
                CancellationToken expansionCancellationToken)
            {
                if (expansionCancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                var selectedCandidateList = selectedCandidates.ToList();
                var traversalDirections = candidatePool.TraversalDirections.Count == 0
                    ? new[] { LinkageRouteTraversalDirection.Forward }
                    : candidatePool.TraversalDirections
                        .OrderBy(traversalDirection => traversalDirection)
                        .ToArray();
                var requiredCandidateInsulationClearance = candidatePool
                    .GroupRequest
                    .GetRequiredInsulationClearance(RouteRequest);
                var repairCacheKey = string.Format(
                    CultureInfo.InvariantCulture,
                    "GROUP={0}||DIRECTIONS={1}||SELECTED={2}||C={3:F6}||MODE={4}",
                    candidatePool.GroupRequest.GroupKey ?? string.Empty,
                    string.Join(",", traversalDirections.Select(
                        traversalDirection => traversalDirection.ToString())),
                    GetCandidateSetSignature(selectedCandidateList),
                    requiredCandidateInsulationClearance,
                    allowDeepClearanceExpansionCandidates ? "DEEP" : "CRITICAL");
                var traversalTimeSliceExpired = 0;
                var expansionCandidateLazy = clearanceExpansionCache.GetOrAdd(
                    repairCacheKey,
                        ignoredCacheKey => new Lazy<IReadOnlyList<LinkageRouteCandidate>>(
                        () =>
                        {
                            // Lazy 只允许首个调用者捕获阶段 token；若该 token 取消，
                            // 下方必须 TryRemove，禁止把截断候选集留给后续阶段。
                            var traversalExpansionCandidates =
                                new List<LinkageRouteCandidate>();
                            foreach (var traversalDirection in traversalDirections)
                            {
                                if (expansionCancellationToken
                                    .IsCancellationRequested)
                                {
                                    break;
                                }

                                using (var traversalCancellationSource =
                                    CancellationTokenSource
                                        .CreateLinkedTokenSource(
                                            expansionCancellationToken))
                                {
                                    if (allowDeepClearanceExpansionCandidates &&
                                        traversalDirections.Length > 1)
                                    {
                                        // 深层正反向候选必须公平获得搜索机会。某一
                                        // 方向无解时不能独占整个请求时限，导致另一个
                                        // 可行方向永远无法启动。
                                        traversalCancellationSource.CancelAfter(
                                            RouteRequest.SearchOptions
                                                .DeepTraversalTimeSliceMilliseconds);
                                    }

                                    try
                                    {
                                        traversalExpansionCandidates.AddRange(
                                            allowDeepClearanceExpansionCandidates
                                                ? LinkageRouteCandidateFactory
                                                    .BuildClearanceDrivenCandidates(
                                                        RouteRequest,
                                                        candidatePool.GroupRequest,
                                                        FixedTerminalAndObstacleSolids,
                                                        selectedCandidateList,
                                                        traversalDirection,
                                                        traversalCancellationSource
                                                            .Token)
                                                : LinkageRouteCandidateFactory
                                                    .BuildCriticalClearanceDrivenCandidates(
                                                        RouteRequest,
                                                        candidatePool.GroupRequest,
                                                        FixedTerminalAndObstacleSolids,
                                                        selectedCandidateList,
                                                        traversalDirection,
                                                        traversalCancellationSource
                                                            .Token));
                                    }
                                    catch (OperationCanceledException) when (
                                        traversalCancellationSource
                                            .IsCancellationRequested)
                                    {
                                        // 单方向时间片结束后继续另一遍历方向；外部取消
                                        // 仍由循环首部和下方统一处理。
                                    }

                                    if (traversalCancellationSource
                                            .IsCancellationRequested &&
                                        !expansionCancellationToken
                                            .IsCancellationRequested)
                                    {
                                        Interlocked.Exchange(
                                            ref traversalTimeSliceExpired,
                                            1);
                                    }
                                }

                            }

                            return traversalExpansionCandidates
                                .GroupBy(LinkageRouteCandidateFactory
                                    .GetCandidateSignature)
                                .Select(candidateGroup => candidateGroup
                                    .OrderBy(routeCandidate => routeCandidate
                                        .PlanarConversion.ActualCopperLength)
                                    .ThenBy(routeCandidate =>
                                        routeCandidate.GeneratedBusbarPartCount)
                                    .First())
                                .OrderBy(routeCandidate => routeCandidate
                                    .PlanarConversion.ActualCopperLength)
                                .ThenBy(routeCandidate =>
                                    routeCandidate.GeneratedBusbarPartCount)
                                .ToList();
                        },
                        LazyThreadSafetyMode.ExecutionAndPublication));
                var expansionCandidates = expansionCandidateLazy.Value;
                if (expansionCancellationToken.IsCancellationRequested ||
                    Volatile.Read(ref traversalTimeSliceExpired) != 0)
                {
                    // 阶段取消或单方向时间片中止得到的候选集合可能不完整，
                    // 不能污染后续分支或更深层阶段缓存。
                    clearanceExpansionCache.TryRemove(
                        repairCacheKey,
                        out var ignoredCancelledExpansionCandidates);
                }

                return expansionCancellationToken.IsCancellationRequested
                    ? Array.Empty<LinkageRouteCandidate>()
                    : expansionCandidates;
            }

            public IReadOnlyList<LinkageRouteCandidate>
                GetPairViolationDirectedCandidates(
                LinkageRouteCandidate sourceRouteCandidate,
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                PairClearanceEvaluation sourcePairEvaluation,
                CancellationToken repairCancellationToken)
            {
                if (repairCancellationToken.IsCancellationRequested ||
                    sourceRouteCandidate == null ||
                    sourcePairEvaluation?.SuggestedSeparationDirection == null ||
                    sourcePairEvaluation.SuggestedSeparationDistance <= 1e-8d)
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                var selectedCandidateList = (selectedCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedCandidate => selectedCandidate != null)
                    .ToList();
                var separationDirection = sourcePairEvaluation
                    .SuggestedSeparationDirection;
                var directedRepairCacheKey = string.Format(
                    CultureInfo.InvariantCulture,
                    "SOURCE={0}||SELECTED={1}||D={2:F6},{3:F6},{4:F6}||DISTANCE={5:F6}",
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        sourceRouteCandidate),
                    GetCandidateSetSignature(selectedCandidateList),
                    separationDirection.X,
                    separationDirection.Y,
                    separationDirection.Z,
                    sourcePairEvaluation.SuggestedSeparationDistance);
                if (pairViolationRepairCache.TryGetValue(
                    directedRepairCacheKey,
                    out var cachedDirectedRepairCandidates))
                {
                    return cachedDirectedRepairCandidates;
                }

                var directedRepairCandidatePool = LinkageRouteCandidateFactory
                    .BuildPairViolationDirectedCandidatePool(
                        RouteRequest,
                        sourceRouteCandidate,
                        FixedTerminalAndObstacleSolids,
                        selectedCandidateList,
                        separationDirection,
                        sourcePairEvaluation.SuggestedSeparationDistance,
                        repairCancellationToken);
                var directedRepairCandidates = directedRepairCandidatePool
                    .ValidCandidates
                    .ToList();
                Interlocked.Increment(
                    ref PairViolationRepairGenerationCount);
                Interlocked.Add(
                    ref PairViolationRepairCandidateCount,
                    directedRepairCandidates.Count);
                if (directedRepairCandidates.Count == 0)
                {
                    var pairRepairFailedCandidate = directedRepairCandidatePool
                        .BestFailedCandidate;
                    var pairRepairFailureDiagnostic = pairRepairFailedCandidate == null
                        ? "未形成可校验位移骨架"
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:{1}",
                            pairRepairFailedCandidate.FailureStage,
                            pairRepairFailedCandidate.Diagnostic);
                    if (Interlocked.CompareExchange(
                            ref PairViolationFirstFailureDiagnostic,
                            pairRepairFailureDiagnostic,
                            null) == null)
                    {
                        Progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = AlgorithmKind,
                            ElapsedMilliseconds =
                                SearchStopwatch.ElapsedMilliseconds,
                            ProcessedStateCount = Interlocked.Read(
                                ref ProcessedStateCount),
                            Message = "定向极限位移首个候选失败：" +
                                pairRepairFailureDiagnostic
                        });
                    }
                }
                if (!repairCancellationToken.IsCancellationRequested)
                {
                    pairViolationRepairCache.TryAdd(
                        directedRepairCacheKey,
                        directedRepairCandidates);
                }

                return repairCancellationToken.IsCancellationRequested
                    ? Array.Empty<LinkageRouteCandidate>()
                    : directedRepairCandidates;
            }

            public bool ShouldPrune(
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                IReadOnlyList<LinkageRouteCandidatePool>
                    remainingCandidatePools)
            {
                var selectedCandidateList = selectedCandidates.ToList();
                var optimisticPartCount = selectedCandidateList.Sum(
                    candidate => candidate.GeneratedBusbarPartCount) +
                    (remainingCandidatePools?.Count ?? 0);
                var optimisticLength = selectedCandidateList.Sum(
                        candidate => candidate.PlanarConversion.ActualCopperLength) +
                    GetRemainingMinimumLengthLowerBound(
                        remainingCandidatePools);
                var maximumSolutionCount = Math.Max(
                    1,
                    RouteRequest.SearchOptions.MaximumSolutionsPerAlgorithm);
                if (Volatile.Read(ref retainedSearchSolutionCount) <
                    maximumSolutionCount)
                {
                    return false;
                }

                var worstLength = Volatile.Read(
                    ref worstRetainedSearchSolutionLength);
                if (optimisticLength > worstLength + 1e-8d)
                {
                    return true;
                }

                return Math.Abs(optimisticLength - worstLength) <= 1e-8d &&
                    optimisticPartCount > Volatile.Read(
                        ref worstRetainedSearchSolutionPartCount);
            }

            private static double GetRemainingMinimumLengthLowerBound(
                IEnumerable<LinkageRouteCandidatePool> remainingCandidatePools)
            {
                return (remainingCandidatePools ??
                        Enumerable.Empty<LinkageRouteCandidatePool>())
                    .Where(candidatePool =>
                        candidatePool?.GroupRequest?.StartTerminal?.RouteAnchor !=
                            null &&
                        candidatePool.GroupRequest.EndTerminal?.RouteAnchor != null)
                    .Sum(candidatePool => candidatePool.GroupRequest.StartTerminal
                        .RouteAnchor.DistanceTo(
                            candidatePool.GroupRequest.EndTerminal.RouteAnchor));
            }

            public bool TryAddCompleteSolution(
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                bool isOptimizationResult = false,
                double sourceSolutionLength = 0d,
                string optimizationSourceTopologySignature = null,
                CancellationToken searchStageCancellationToken = default(CancellationToken))
            {
                if (CancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var selectedCandidateList = selectedCandidates.ToList();
                if (RouteRequest
                        .UseLegacyDoubleStartHorizontalTopologyProposal &&
                    !TryMaterializeLegacyTopologyProposal(
                        selectedCandidateList,
                        out selectedCandidateList))
                {
                    return false;
                }
                var completeCandidateGroupCenterlines =
                    CreateCandidateGroupCenterlines(selectedCandidateList);
                var completeRouteTopologySignature =
                    LinkageRouteTopologySignature.CreateSolutionSignature(
                        completeCandidateGroupCenterlines);
                if (!isOptimizationResult &&
                    publishedCompleteTopologySignatures.ContainsKey(
                        completeRouteTopologySignature))
                {
                    return false;
                }

                if (!isOptimizationResult &&
                    TryGetPublishedRouteFamily(completeCandidateGroupCenterlines,
                        out var publishedFamilyLineage, out var publishedFamilyBestLength))
                {
                    // 同族已发布：不另起新方案。更短时按该族谱系的优化结果处理，
                    // 走同一完整门禁后替换该族代表（界面同一行更新）。
                    if (string.IsNullOrWhiteSpace(publishedFamilyLineage) ||
                        selectedCandidateList.Sum(selectedRouteCandidate =>
                            selectedRouteCandidate.PlanarConversion.ActualCopperLength) + 1e-8d >=
                            publishedFamilyBestLength)
                        return false;
                    isOptimizationResult = true;
                    optimizationSourceTopologySignature = publishedFamilyLineage;
                    sourceSolutionLength = publishedFamilyBestLength;
                }

                var retainedRouteTopologySignature = isOptimizationResult &&
                    !string.IsNullOrWhiteSpace(
                        optimizationSourceTopologySignature)
                        ? optimizationSourceTopologySignature
                        : completeRouteTopologySignature;

                var completeCandidateActualCopperLength = selectedCandidateList.Sum(
                    selectedRouteCandidate => selectedRouteCandidate
                        .PlanarConversion.ActualCopperLength);
                var completeCandidateGeneratedBusbarPartCount =
                    selectedCandidateList.Sum(selectedRouteCandidate =>
                        selectedRouteCandidate.GeneratedBusbarPartCount);
                var completeCandidateStableGeometrySignature =
                    LinkageRouteTopologySignature.CreateStableGeometrySignature(
                        AlgorithmKind,
                        completeCandidateGroupCenterlines);
                lock (resultGate)
                {
                    if (!isOptimizationResult &&
                        publishedCompleteTopologySignatures.ContainsKey(
                            completeRouteTopologySignature))
                    {
                        return false;
                    }

                    if (successfulOutputSourceSearchSolutions.TryGetValue(
                            retainedRouteTopologySignature,
                            out var precalculationTopologyRepresentative) &&
                        !LinkageRouteTopologySignature.IsPreferredRepresentative(
                            completeCandidateActualCopperLength,
                            completeCandidateGeneratedBusbarPartCount,
                            CountCandidateSetBends(selectedCandidateList),
                            completeCandidateStableGeometrySignature,
                            precalculationTopologyRepresentative))
                    {
                        return false;
                    }
                }

                // 廉价代表预筛完成后，才执行普通方案的实际逐排终检。
                // 开关关闭时保持原有执行时序；开关开启时这里仍完全使用普通候选。
                var ordinaryCompleteSolution = CreateSuccessSolution(
                    this,
                    selectedCandidateList);
                if (ordinaryCompleteSolution == null ||
                    CancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                ordinaryCompleteSolution.OptimizationSourceTopologySignature =
                    isOptimizationResult
                        ? optimizationSourceTopologySignature
                        : null;

                lock (resultGate)
                {
                    if (!isOptimizationResult &&
                        publishedCompleteTopologySignatures.ContainsKey(
                            completeRouteTopologySignature))
                    {
                        return false;
                    }

                    if (successfulOutputSourceSearchSolutions.TryGetValue(
                            retainedRouteTopologySignature,
                            out var currentTopologyRepresentative) &&
                        !LinkageRouteTopologySignature.IsPreferredRepresentative(
                            ordinaryCompleteSolution,
                            currentTopologyRepresentative))
                    {
                        return false;
                    }

                    successfulSearchSolutions[retainedRouteTopologySignature] =
                        ordinaryCompleteSolution;
                    successfulCandidateSets[retainedRouteTopologySignature] =
                        selectedCandidateList.ToList();
                    var maximumSolutionCount = Math.Max(
                        1,
                        RouteRequest.SearchOptions.MaximumSolutionsPerAlgorithm);
                    var retainedSearchTopologySignatures = new HashSet<string>(
                        successfulSearchSolutions
                        .OrderBy(solutionPair => solutionPair.Value.ActualCopperLength)
                        .ThenBy(solutionPair =>
                            solutionPair.Value.GeneratedBusbarPartCount)
                        .ThenBy(solutionPair => LinkageRouteTopologySignature
                            .CreateStableGeometrySignature(solutionPair.Value),
                            StringComparer.Ordinal)
                        .ThenBy(solutionPair => solutionPair.Key, StringComparer.Ordinal)
                        .Take(maximumSolutionCount)
                        .Select(solutionPair => solutionPair.Key),
                        StringComparer.Ordinal);
                    foreach (var removedSignature in successfulSearchSolutions.Keys
                        .Where(existingSignature =>
                            !retainedSearchTopologySignatures.Contains(
                            existingSignature))
                        .ToList())
                    {
                        successfulSearchSolutions.Remove(removedSignature);
                        successfulCandidateSets.Remove(removedSignature);
                    }

                    UpdateSearchPruningThreshold(maximumSolutionCount);

                }

                // 普通缓存 Top-N 只限制来源引用的存储，不是发布资格。
                // 未验证的短候选不能挤掉其它尚待完整发布终检的候选。

                var requiresTerminalOutputPostProcessing =
                    RequiresTerminalOutputPostProcessing(
                        selectedCandidateList);
                if (requiresTerminalOutputPostProcessing)
                {
                    // 普通方案只作为已经通过完整终检的内部输入。启用任一
                    // 终端选项时，只允许发布统一后处理并再次通过实体终检
                    // 的输出，不能先泄漏满搭接或带垫块的半处理结果。
                    ScheduleTerminalOutputPostProcessing(
                        retainedRouteTopologySignature,
                        ordinaryCompleteSolution,
                        isOptimizationResult,
                        sourceSolutionLength,
                        optimizationSourceTopologySignature,
                        selectedCandidateList,
                        searchStageCancellationToken);
                    // 异步任务只登记候选，真正成功由发布集合确认。
                    return false;
                }
                return TryPublishOutputSolution(
                    retainedRouteTopologySignature,
                    ordinaryCompleteSolution,
                    ordinaryCompleteSolution,
                    isOptimizationResult,
                    sourceSolutionLength,
                    optimizationSourceTopologySignature,
                    selectedCandidateList);
            }

            private bool RequiresMultiStartConnectionPlateOutputAlignment(
                LinkageRouteCandidate selectedCandidate)
            {
                var planarConversion = selectedCandidate?.PlanarConversion;
                var fixedStartCount = selectedCandidate?.GroupRequest?
                    .StartTerminal?.Members?.Count ?? 0;
                var requiresConnectionAlignment =
                    (fixedStartCount >= 2 && OutputRouteRequest.AllowDoubleStartMiddleLap) ||
                    (fixedStartCount == 1 && OutputRouteRequest.EnableSingleStartConnectionBar);
                if (!requiresConnectionAlignment || planarConversion == null)
                {
                    return false;
                }

                return planarConversion.DoubleStartPhysicalBridgeSectionIndices
                           .Count > 0 ||
                    planarConversion.Accessories.Any(routeAccessory =>
                        routeAccessory?.Kind == LinkageRouteAccessoryKind
                            .DoubleStartHorizontalBridge) ||
                    (selectedCandidate.TopologySignature ?? string.Empty)
                        .IndexOf("DOUBLE-START-L-BRIDGE",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (selectedCandidate.TopologySignature ?? string.Empty)
                        .IndexOf("DOUBLE-START-I-BRIDGE",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                    planarConversion.StartStackPlacement?
                        .DoubleStartHorizontalBridgeGeneratedMemberIndex
                        .HasValue == true ||
                    planarConversion.StartStackPlacement?
                        .DoubleStartHorizontalBridgePlacements?.Count > 0;
            }

            private bool RequiresTerminalOutputPostProcessing(
                IEnumerable<LinkageRouteCandidate> selectedCandidates)
            {
                return OutputRouteRequest.MinimizeSupportBlocks ||
                    OutputRouteRequest.RequiresOutputPostProcessing() ||
                    OutputRouteRequest.Groups.Any(routeGroupRequest =>
                        routeGroupRequest?.HasSectionSpecificBusbarSettings(
                            OutputRouteRequest) == true) ||
                    (selectedCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>()).Any(
                        RequiresMultiStartConnectionPlateOutputAlignment);
            }

            private LinkageRouteCandidate CreateRequiredTerminalOutputCandidate(
                LinkageMultiGroupRouteRequest validationRouteRequest,
                LinkageRouteCandidate ordinaryRouteCandidate,
                CancellationToken cancellationToken)
            {
                if (OutputRouteRequest.RequiresOutputPostProcessing())
                {
                    return LinkageRouteCandidateFactory
                        .CreateTerminalOutputPostProcessedCandidate(
                            validationRouteRequest,
                            ordinaryRouteCandidate,
                            FixedTerminalAndObstacleSolids,
                            cancellationToken);
                }

                if (RequiresMultiStartConnectionPlateOutputAlignment(
                        ordinaryRouteCandidate))
                {
                    return LinkageRouteCandidateFactory
                        .CreateMultiStartConnectionPlateAlignedOutputCandidate(
                            validationRouteRequest,
                            ordinaryRouteCandidate,
                            FixedTerminalAndObstacleSolids,
                            cancellationToken);
                }

                return ordinaryRouteCandidate;
            }

            private bool TryMaterializeLegacyTopologyProposal(
                IReadOnlyList<LinkageRouteCandidate> legacyTopologyProposals,
                out List<LinkageRouteCandidate> physicalBridgeCandidates)
            {
                physicalBridgeCandidates = new List<LinkageRouteCandidate>();
                var physicalMaterializationRequest = RouteRequest
                    .CreatePhysicalDoubleStartBridgeValidationRequest()
                    .CreateRequestedClearanceValidationRequest();
                foreach (var legacyTopologyProposal in
                    legacyTopologyProposals ??
                        Array.Empty<LinkageRouteCandidate>())
                {
                    if (CancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    var physicalBridgeCandidate = LinkageRouteCandidateFactory
                        .RebuildLegacyTopologyProposalWithPhysicalBridge(
                            physicalMaterializationRequest,
                            legacyTopologyProposal,
                            FixedTerminalAndObstacleSolids,
                            CancellationToken);
                    if (physicalBridgeCandidate?.IsValid != true)
                    {
                        return false;
                    }

                    foreach (var selectedPhysicalBridgeCandidate in
                        physicalBridgeCandidates)
                    {
                        if (!EvaluateCandidatePair(
                                physicalBridgeCandidate,
                                selectedPhysicalBridgeCandidate,
                                physicalMaterializationRequest
                                    .InsulationClearance,
                                CancellationToken).IsClear)
                        {
                            return false;
                        }
                    }

                    physicalBridgeCandidates.Add(physicalBridgeCandidate);
                }

                return physicalBridgeCandidates.Count ==
                    (legacyTopologyProposals?.Count ?? 0);
            }

            private void UpdateSearchPruningThreshold(int maximumSolutionCount)
            {
                Volatile.Write(
                    ref retainedSearchSolutionCount,
                    successfulSolutions.Count);
                if (successfulSolutions.Count < maximumSolutionCount)
                {
                    Volatile.Write(
                        ref worstRetainedSearchSolutionLength,
                        double.PositiveInfinity);
                    Volatile.Write(
                        ref worstRetainedSearchSolutionPartCount,
                        int.MaxValue);
                    return;
                }

                // 仅已通过完整发布门禁的来源提供上界。最终调整可缩短或
                // 补长实体，取最终与来源长度较大者，避免新增更紧的剪枝。
                var worstRetainedSearchSolution = successfulSolutions.Select(pair =>
                    new
                    {
                        Length = Math.Max(pair.Value.ActualCopperLength,
                            successfulOutputSourceSearchSolutions.TryGetValue(pair.Key, out var verifiedSource)
                                ? verifiedSource.ActualCopperLength : pair.Value.ActualCopperLength),
                        Parts = Math.Max(pair.Value.GeneratedBusbarPartCount,
                            successfulOutputSourceSearchSolutions.TryGetValue(pair.Key, out var verifiedPartSource)
                                ? verifiedPartSource.GeneratedBusbarPartCount : pair.Value.GeneratedBusbarPartCount)
                    })
                    .OrderBy(searchSolution => searchSolution.Length)
                    .ThenBy(searchSolution => searchSolution.Parts)
                    .Last();
                Volatile.Write(
                    ref worstRetainedSearchSolutionLength,
                    worstRetainedSearchSolution.Length);
                Volatile.Write(
                    ref worstRetainedSearchSolutionPartCount,
                    worstRetainedSearchSolution.Parts);
            }

            private void ScheduleTerminalOutputPostProcessing(
                string sourceRouteTopologySignature,
                LinkageRouteSolution sourceOrdinarySearchSolution,
                bool isOptimizationResult,
                double sourceSolutionLength,
                string optimizationSourceTopologySignature,
                IReadOnlyList<LinkageRouteCandidate> ordinaryCandidateList,
                CancellationToken searchStageCancellationToken = default(CancellationToken))
            {
                if (CancellationToken.IsCancellationRequested ||
                    Volatile.Read(
                        ref acceptsTerminalOutputPostProcessingTasks) == 0 ||
                    sourceOrdinarySearchSolution == null ||
                    ordinaryCandidateList == null ||
                    ordinaryCandidateList.Count == 0)
                {
                    DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                    return;
                }

                if (!TryRegisterBackgroundTask(true))
                {
                    DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                    return;
                }
                var reservedOutputQueueSlot = false;
                try
                {
                    // 在创建任务及深拷贝快照之前施加可取消背压，不以普通
                    // Top-N 淘汰尚未通过终检的不同几何候选。等待同时服从
                    // 搜索阶段时间片：队列满时阶段到点即结束，未入队组合按
                    // 下方拒绝路径丢弃，后续阶段清空访问状态后仍可重新找到。
                    if (searchStageCancellationToken.CanBeCanceled)
                    {
                        using (var queueWaitCancellationSource =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                CancellationToken,
                                searchStageCancellationToken))
                        {
                            terminalOutputQueueSlots.Wait(queueWaitCancellationSource.Token);
                        }
                    }
                    else
                    {
                        terminalOutputQueueSlots.Wait(CancellationToken);
                    }
                    reservedOutputQueueSlot = true;
                    var ordinaryCandidateSnapshot = ordinaryCandidateList.ToList();
                    Task.Run(async () =>
                    {
                        var enteredPostProcessingSemaphore = false;
                        var terminalOutputPublished = false;
                        try
                        {
                            await terminalOutputPostProcessingSemaphore
                                .WaitAsync(CancellationToken)
                                .ConfigureAwait(false);
                            enteredPostProcessingSemaphore = true;
                            if (CancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            var postProcessedCandidateList =
                                new List<LinkageRouteCandidate>();
                            foreach (var ordinaryRouteCandidate in
                                ordinaryCandidateSnapshot)
                            {
                                var postProcessedRouteCandidate =
                                    CreateRequiredTerminalOutputCandidate(
                                        OutputRouteRequest,
                                        ordinaryRouteCandidate,
                                        CancellationToken);
                                if (postProcessedRouteCandidate == null ||
                                    CancellationToken.IsCancellationRequested)
                                {
                                    return;
                                }

                                if (!postProcessedRouteCandidate.IsValid)
                                {
                                    postProcessedRouteCandidate =
                                        CreateTerminalOutputCandidateWithFallback(
                                            OutputRouteRequest,
                                            ordinaryRouteCandidate,
                                            postProcessedCandidateList);
                                }

                                if (postProcessedRouteCandidate?.IsValid != true)
                                {
                                    RecordTerminalOutputPostProcessingFailure(
                                        postProcessedCandidateList,
                                        postProcessedRouteCandidate);
                                    return;
                                }

                                postProcessedCandidateList.Add(
                                    postProcessedRouteCandidate);
                            }

                            if (OutputRouteRequest.MinimizeSupportBlocks)
                            {
                                for (var reductionIndex = 0; reductionIndex < postProcessedCandidateList.Count; reductionIndex++)
                                {
                                    var currentReductionIndex = reductionIndex;
                                    postProcessedCandidateList[reductionIndex] = LinkageRouteCandidateFactory
                                        .CreateSupportReducedOutputCandidate(OutputRouteRequest,
                                            postProcessedCandidateList[reductionIndex], FixedTerminalAndObstacleSolids,
                                            trial => {
                                                var completeTrialCandidates = postProcessedCandidateList.ToList();
                                                completeTrialCandidates[currentReductionIndex] = trial;
                                                return IsPublishablePostProcessedSolution(
                                                    CreateCompleteSolution(this, completeTrialCandidates, false));
                                            }, CancellationToken);
                                }
                            }

                            // 普通方案已经完成全部搜索校验。输出选项只改写
                            // 成功方案深拷贝；这里重新计算总长和实际绝缘距离，
                            // 不得再把规格后处理诊断回传为主搜索失败。
                            var postProcessedCompleteSolution =
                                CreateCompleteSolution(
                                    this,
                                    postProcessedCandidateList,
                                    false);
                            if (postProcessedCompleteSolution != null &&
                                postProcessedCompleteSolution.MinimumClearance +
                                    1e-6d <
                                    OutputRouteRequest.InsulationClearance)
                            {
                                using (var physicalOptimizationCancellationSource =
                                    CancellationTokenSource
                                        .CreateLinkedTokenSource(
                                            CancellationToken))
                                {
                                    physicalOptimizationCancellationSource
                                        .CancelAfter(Math.Max(
                                            1,
                                            OutputRouteRequest.SearchOptions
                                                .ClearanceOptimizationLimitMilliseconds));
                                    var optimizedPhysicalCandidateSet =
                                        TryOptimizeCompleteCandidateSetClearance(
                                            postProcessedCandidateList,
                                            physicalOptimizationCancellationSource
                                                .Token,
                                            out _,
                                            out var optimizedPhysicalClearance);
                                    if (optimizedPhysicalCandidateSet != null &&
                                        optimizedPhysicalClearance >
                                            postProcessedCompleteSolution
                                                .MinimumClearance + 1e-6d)
                                    {
                                        postProcessedCandidateList =
                                            optimizedPhysicalCandidateSet.ToList();
                                        postProcessedCompleteSolution =
                                            CreateCompleteSolution(
                                                this,
                                                postProcessedCandidateList,
                                                false);
                                    }
                                }
                            }
                            if (!IsPublishablePostProcessedSolution(
                                    postProcessedCompleteSolution))
                            {
                                var catalogCandidateSet =
                                    TryFindBestPostProcessedCatalogCandidateSet();
                                var catalogSolution = catalogCandidateSet == null
                                    ? null
                                    : CreateCompleteSolution(
                                        this,
                                        catalogCandidateSet,
                                        false);
                                if (IsPublishablePostProcessedSolution(
                                        catalogSolution) &&
                                    (postProcessedCompleteSolution == null ||
                                     catalogSolution.MinimumClearance >
                                        postProcessedCompleteSolution
                                            .MinimumClearance + 1e-6d))
                                {
                                    postProcessedCompleteSolution =
                                        catalogSolution;
                                }
                            }

                            if (!IsPublishablePostProcessedSolution(
                                    postProcessedCompleteSolution) ||
                                CancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            postProcessedCompleteSolution
                                .RequestedInsulationClearance =
                                    OutputRouteRequest.InsulationClearance;
                            postProcessedCompleteSolution
                                .RequestedLapInsulationClearance =
                                    OutputRouteRequest.LapInsulationClearance;
                            postProcessedCompleteSolution
                                .SearchInsulationClearance =
                                    RouteRequest.InsulationClearance;
                            postProcessedCompleteSolution
                                .IsRelaxedClearanceSolution =
                                    !IsFinalRequestedClearanceSatisfied(
                                        postProcessedCompleteSolution.MinimumClearance,
                                        OutputRouteRequest.InsulationClearance);

                            terminalOutputPublished = TryPublishOutputSolution(
                                sourceRouteTopologySignature,
                                sourceOrdinarySearchSolution,
                                postProcessedCompleteSolution,
                                isOptimizationResult,
                                sourceSolutionLength,
                                optimizationSourceTopologySignature,
                                ordinaryCandidateSnapshot);
                        }
                        catch (OperationCanceledException) when (
                            CancellationToken.IsCancellationRequested)
                        {
                        }
                        catch (Exception postProcessingException)
                        {
                            Progress?.Report(new LinkageRouteProgress
                            {
                                AlgorithmKind = AlgorithmKind,
                                ElapsedMilliseconds =
                                    SearchStopwatch.ElapsedMilliseconds,
                                ProcessedStateCount = Interlocked.Read(
                                    ref ProcessedStateCount),
                                Message = "终端输出后处理执行异常：" +
                                    postProcessingException.Message
                            });
                        }
                        finally
                        {
                            if (!terminalOutputPublished)
                                DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                            if (enteredPostProcessingSemaphore)
                            {
                                terminalOutputPostProcessingSemaphore.Release();
                            }

                            terminalOutputQueueSlots.Release();
                            FinishBackgroundTask(true);
                        }
                    });
                }
                catch (OperationCanceledException) when (
                    CancellationToken.IsCancellationRequested ||
                    searchStageCancellationToken.IsCancellationRequested)
                {
                    DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                    if (reservedOutputQueueSlot)
                        terminalOutputQueueSlots.Release();
                    FinishBackgroundTask(true);
                }
                catch (Exception schedulingException)
                {
                    DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                    if (reservedOutputQueueSlot)
                        terminalOutputQueueSlots.Release();
                    FinishBackgroundTask(true);

                    Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = AlgorithmKind,
                        ElapsedMilliseconds = SearchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref ProcessedStateCount),
                        Message = "无法创建终端输出后处理任务：" +
                            schedulingException.Message
                    });
                }
            }

            private bool IsSearchTopologyRetained(
                string sourceRouteTopologySignature)
            {
                lock (resultGate)
                {
                    return !string.IsNullOrWhiteSpace(
                            sourceRouteTopologySignature) &&
                        successfulSearchSolutions.ContainsKey(
                            sourceRouteTopologySignature);
                }
            }

            private bool TryPublishOutputSolution(
                string sourceRouteTopologySignature,
                LinkageRouteSolution sourceOrdinarySearchSolution,
                LinkageRouteSolution outputSolution,
                bool isOptimizationResult,
                double sourceSolutionLength,
                string optimizationSourceTopologySignature,
                IReadOnlyList<LinkageRouteCandidate> publicationSourceCandidates)
            {
                var actualOutputPublished = false;
                try
                {
                    actualOutputPublished = TryPublishValidatedOutputSolution(
                        sourceRouteTopologySignature, sourceOrdinarySearchSolution, outputSolution,
                        isOptimizationResult, sourceSolutionLength, optimizationSourceTopologySignature,
                        publicationSourceCandidates);
                    if (actualOutputPublished)
                        ActivatePublishedSolutionOptimizations(sourceRouteTopologySignature,
                            sourceOrdinarySearchSolution, publicationSourceCandidates);
                    return actualOutputPublished;
                }
                finally
                {
                    if (!actualOutputPublished)
                        DiscardRejectedSearchRepresentative(sourceRouteTopologySignature, sourceOrdinarySearchSolution);
                }
            }

            private void DiscardRejectedSearchRepresentative(string rejectedTopologySignature,
                LinkageRouteSolution rejectedSearchSolution)
            {
                if (string.IsNullOrWhiteSpace(rejectedTopologySignature) || rejectedSearchSolution == null)
                    return;
                lock (resultGate)
                {
                    // 迟到失败只能清理其自身代表，不能删除后来提交的候选。
                    if (!successfulSearchSolutions.TryGetValue(rejectedTopologySignature, out var currentRepresentative) ||
                        !ReferenceEquals(currentRepresentative, rejectedSearchSolution))
                        return;
                    if (successfulSolutions.ContainsKey(rejectedTopologySignature) &&
                        successfulOutputSourceSearchSolutions.TryGetValue(rejectedTopologySignature, out var verifiedRepresentative))
                    {
                        successfulSearchSolutions[rejectedTopologySignature] = verifiedRepresentative;
                        if (publishedCandidateSets.TryGetValue(rejectedTopologySignature, out var verifiedCandidates))
                            successfulCandidateSets[rejectedTopologySignature] = verifiedCandidates;
                        else
                            successfulCandidateSets.Remove(rejectedTopologySignature);
                    }
                    else
                    {
                        successfulSearchSolutions.Remove(rejectedTopologySignature);
                        successfulCandidateSets.Remove(rejectedTopologySignature);
                        // 该集合只写入真实发布历史。Top-N 淘汰后，迟到拒绝
                        // 仍不能撤销先前已验证拓扑的永久去重记录。
                    }
                    UpdateSearchPruningThreshold(Math.Max(1, RouteRequest.SearchOptions.MaximumSolutionsPerAlgorithm));
                }
            }

            private void ActivatePublishedSolutionOptimizations(string publishedTopologySignature,
                LinkageRouteSolution verifiedSearchSource,
                IReadOnlyList<LinkageRouteCandidate> verifiedCandidateSet)
            {
                bool switchToPublishedSolutionAllocation;
                lock (resultGate)
                {
                    if (!successfulSolutions.ContainsKey(publishedTopologySignature) ||
                        !successfulOutputSourceSearchSolutions.TryGetValue(publishedTopologySignature, out var currentVerifiedSource) ||
                        !ReferenceEquals(currentVerifiedSource, verifiedSearchSource))
                        return;
                    switchToPublishedSolutionAllocation = successfulSolutions.Count >=
                        RouteRequest.SearchOptions.SolutionAllocationSwitchSolutionCount;
                }
                Interlocked.Exchange(ref lastRetainedSolutionElapsedMilliseconds, SearchStopwatch.ElapsedMilliseconds);
                if (Volatile.Read(ref optimizationPriorityAllocationActivated) != 0)
                    RefreshPreferredSolutionOptimizations();
                else if (switchToPublishedSolutionAllocation)
                {
                    TryActivatePostSwitchSolutionAllocation();
                    RefreshPreferredSolutionOptimizations();
                }
                else
                    TryActivateInitialSolutionOptimizationAllocation();
                // 探索期：新方案进入最短前列时，取消被挤出者的优化，让出线程优化新的最短方案。
                if (Volatile.Read(ref optimizationPriorityAllocationActivated) == 0 &&
                    !switchToPublishedSolutionAllocation && IsExplorationWindowActive)
                    RefreshPreferredSolutionOptimizations();
                if (Volatile.Read(ref postSwitchSolutionAllocationActivated) == 0 && verifiedCandidateSet?.Count > 0)
                    ScheduleSolutionOptimization(GetCandidateSetSignature(verifiedCandidateSet),
                        publishedTopologySignature, verifiedCandidateSet);
            }

            private bool TryPublishValidatedOutputSolution(
                string sourceRouteTopologySignature,
                LinkageRouteSolution sourceOrdinarySearchSolution,
                LinkageRouteSolution outputSolution,
                bool isOptimizationResult,
                double sourceSolutionLength,
                string optimizationSourceTopologySignature,
                IReadOnlyList<LinkageRouteCandidate> publicationSourceCandidates)
            {
                if (sourceOrdinarySearchSolution == null ||
                    outputSolution == null ||
                    string.IsNullOrWhiteSpace(sourceRouteTopologySignature))
                {
                    return false;
                }

                // 这是进入成功方案集合的最后一道实体门禁。发布前必须再跑
                // 一次完整的十轮联动回归，随后严格复核叠排、连接排、搭接
                // 闭合和正体积干涉；最终几何仍错误的候选不得进入结果列表。
                // 同步入口可能把同一对象作为搜索来源和输出。拆U与后续联动
                // 只能修改独立输出，不能污染用于代表比较和剪枝的普通来源。
                if (ReferenceEquals(sourceOrdinarySearchSolution, outputSolution))
                    outputSolution = CloneSolutionSnapshot(outputSolution);
                if (!TryApplyUShapeOutputPolicy(OutputRouteRequest, outputSolution,
                        out _, out var outputUShapeFailureReason))
                {
                    lock (resultGate)
                    {
                        if (firstFinalOutputRejectionReason == null)
                            firstFinalOutputRejectionReason = outputUShapeFailureReason;
                    }
                    return false;
                }
                ApplyPhysicalSolutionOutputRegressionAdjustments(
                    outputSolution,
                    OutputRouteRequest);
                if (!TryValidateFinalPhysicalSolution(
                        OutputRouteRequest,
                        outputSolution,
                        out var finalOutputRejectionReason))
                {
                    lock (resultGate)
                    {
                        if (firstFinalOutputRejectionReason == null)
                            firstFinalOutputRejectionReason = finalOutputRejectionReason;
                    }
                    return false;
                }
                // Earlier adjustment rounds may have recorded a temporary
                // structure failure. The final strict gate has now verified
                // the actual output solids, so that stale failure must not
                // be shown alongside a successful scheme.
                for (var diagnosticIndex = outputSolution.Diagnostics.Count - 1;
                    diagnosticIndex >= 0;
                    diagnosticIndex--)
                {
                    var diagnostic = outputSolution.Diagnostics[diagnosticIndex];
                    if (diagnostic?.Stage == LinkageRouteFailureStage.Conversion &&
                        diagnostic.Message?.StartsWith(
                            "最终输出连续 10 轮联动回归调整后仍存在正体积自相交，方案已保留并标记错误：",
                            StringComparison.Ordinal) == true)
                    {
                        outputSolution.Diagnostics.RemoveAt(diagnosticIndex);
                    }
                }
                if (!TryGetPhysicalSolutionMinimumClearance(
                        OutputRouteRequest,
                        outputSolution,
                        out var exactOutputMinimumClearance))
                {
                    return false;
                }
                var innerGapOutput = CloneSolutionSnapshot(outputSolution);
                var originalInnerEndCount = outputSolution.GroupSolutions.Sum(group =>
                    LinkageRouteSectionSpecificationPostProcessor.CountInternalLapEndsInGaps(CreateOutputAdjustmentConversion(group)));
                var inwardEndCount = 0;
                foreach (var innerGapGroup in innerGapOutput.GroupSolutions)
                {
                    var sourceGapGroup = outputSolution.GroupSolutions.First(group => group.GroupKey == innerGapGroup.GroupKey);
                    for (var gapAccessoryIndex = 0; gapAccessoryIndex < innerGapGroup.Accessories.Count; gapAccessoryIndex++)
                        innerGapGroup.Accessories[gapAccessoryIndex].SupportBlockKey = sourceGapGroup.Accessories[gapAccessoryIndex].SupportBlockKey;
                    inwardEndCount += LinkageRouteSectionSpecificationPostProcessor.MoveInternalLapEndsIntoAvailableGaps(
                        CreateOutputAdjustmentConversion(innerGapGroup));
                }
                if (inwardEndCount > 0)
                {
                    ApplyPhysicalSolutionOutputRegressionAdjustments(innerGapOutput, OutputRouteRequest);
                    var retainedInnerEndCount = innerGapOutput.GroupSolutions.Sum(group =>
                        LinkageRouteSectionSpecificationPostProcessor.CountInternalLapEndsInGaps(CreateOutputAdjustmentConversion(group))) -
                        originalInnerEndCount;
                    if (retainedInnerEndCount > 0 &&
                        TryValidateFinalPhysicalSolution(OutputRouteRequest, innerGapOutput, out _) &&
                        TryGetPhysicalSolutionMinimumClearance(OutputRouteRequest, innerGapOutput, out var innerGapClearance) &&
                        innerGapClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= OutputRouteRequest.InsulationClearance)
                    {
                        var globalMinimumThickness = LinkageRouteCopperLength.GetGlobalMinimumThickness(OutputRouteRequest, innerGapOutput);
                        var beforeSupportLength = outputSolution.GroupSolutions.Sum(group =>
                            LinkageRouteCopperLength.GetCountedSupportLength(group.Accessories, globalMinimumThickness));
                        var afterSupportLength = innerGapOutput.GroupSolutions.Sum(group =>
                            LinkageRouteCopperLength.GetCountedSupportLength(group.Accessories, globalMinimumThickness));
                        var beforeCopperLength = outputSolution.ActualCopperLength;
                        outputSolution = innerGapOutput;
                        exactOutputMinimumClearance = innerGapClearance;
                        AddOutputAdjustmentDiagnostic(outputSolution, null,
                            string.Format(CultureInfo.InvariantCulture,
                                "内侧叠排缝隙优化：保留 {0} 个内侧搭接端；全局最薄铜排 {1:F3}mm；计长垫块 {2:F3}→{3:F3}mm；含计长垫块总长 {4:F3}→{5:F3}mm。",
                                retainedInnerEndCount, globalMinimumThickness, beforeSupportLength, afterSupportLength,
                                beforeCopperLength, outputSolution.ActualCopperLength));
                    }
                }
                outputSolution.MinimumClearance = exactOutputMinimumClearance;
                if (!IsPublishablePostProcessedSolution(outputSolution))
                {
                    return false;
                }

                outputSolution.OptimizationSourceTopologySignature =
                    isOptimizationResult
                        ? optimizationSourceTopologySignature
                        : null;
                outputSolution.RequestedInsulationClearance =
                    OutputRouteRequest.InsulationClearance;
                outputSolution.RequestedLapInsulationClearance =
                    OutputRouteRequest.LapInsulationClearance;
                outputSolution.SearchInsulationClearance =
                    RouteRequest.InsulationClearance;
                outputSolution.IsRelaxedClearanceSolution =
                    outputSolution.MinimumClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                        OutputRouteRequest.InsulationClearance;

                string evictedFamilyLineage = null;
                lock (outputPublicationGate)
                {
                    LinkageRouteSolution publishedSolutionSnapshot;
                    lock (resultGate)
                    {
                        // 更新的待验证代表不能屏蔽已通过完整终检的旧候选。
                        // 只与已经实际发布的来源比较，防止覆盖有效的新代表。
                        if (successfulOutputSourceSearchSolutions.TryGetValue(
                                sourceRouteTopologySignature,
                                out var currentOutputSourceSearchSolution) &&
                            !ReferenceEquals(
                                sourceOrdinarySearchSolution,
                                currentOutputSourceSearchSolution) &&
                            !LinkageRouteTopologySignature
                                .IsPreferredRepresentative(
                                    sourceOrdinarySearchSolution,
                                    currentOutputSourceSearchSolution))
                        {
                            return false;
                        }

                        // 路径族判重：并行验证中的同族方案在此统一裁决。普通方案遇到已发布
                        // 同族（不论是否仍在前 N）一律不再发布，其候选只登记给优化目录；优化
                        // 结果落入另一仍在列谱系的路径族时，只有更优才发布并替下对方。
                        var publicationFamilySignatures = new List<string>();
                        if (usesRouteFamilyScheduling)
                            publicationFamilySignatures.Add(CreateRouteFamilySignature(outputSolution));
                        if (usesRouteFamilyScheduling && publicationSourceCandidates != null)
                            publicationFamilySignatures.Add(CreateRouteFamilySignature(
                                CreateCandidateGroupCenterlines(publicationSourceCandidates)));
                        string conflictingFamilyLineage = null;
                        foreach (var publicationFamilySignature in publicationFamilySignatures
                            .Where(familySignature => !string.IsNullOrEmpty(familySignature))
                            .Distinct(StringComparer.Ordinal))
                        {
                            if (publishedRouteFamilyLineages.TryGetValue(
                                    publicationFamilySignature, out var familyLineage) &&
                                !string.Equals(familyLineage, sourceRouteTopologySignature,
                                    StringComparison.Ordinal) &&
                                (!isOptimizationResult || successfulSolutions.ContainsKey(familyLineage)))
                            {
                                conflictingFamilyLineage = familyLineage;
                                break;
                            }
                        }

                        if (conflictingFamilyLineage != null)
                        {
                            if (!isOptimizationResult ||
                                (successfulOutputSourceSearchSolutions.TryGetValue(
                                     conflictingFamilyLineage, out var conflictingFamilySource) &&
                                 !LinkageRouteTopologySignature.IsPreferredRepresentative(
                                     sourceOrdinarySearchSolution, conflictingFamilySource)))
                            {
                                foreach (var duplicateFamilyCandidate in publicationSourceCandidates ??
                                    Array.Empty<LinkageRouteCandidate>())
                                    RegisterSolutionOptimizationCandidate(duplicateFamilyCandidate);
                                return false;
                            }

                            successfulSolutions.Remove(conflictingFamilyLineage);
                            successfulOutputSourceSearchSolutions.Remove(conflictingFamilyLineage);
                            publishedCandidateSets.Remove(conflictingFamilyLineage);
                            evictedFamilyLineage = conflictingFamilyLineage;
                        }

                        successfulSolutions[sourceRouteTopologySignature] =
                            outputSolution;
                        successfulOutputSourceSearchSolutions[
                            sourceRouteTopologySignature] =
                                sourceOrdinarySearchSolution;
                        if (publicationSourceCandidates != null)
                            publishedCandidateSets[sourceRouteTopologySignature] = publicationSourceCandidates.ToList();

                        var maximumOutputSolutionCount = Math.Max(
                            1,
                            OutputRouteRequest.SearchOptions
                                .MaximumSolutionsPerAlgorithm);
                        var retainedOutputTopologySignatures = new HashSet<string>(
                            successfulSolutions
                                .OrderBy(solutionPair =>
                                    solutionPair.Value.ActualCopperLength)
                                .ThenBy(solutionPair =>
                                    solutionPair.Value.GeneratedBusbarPartCount)
                                .ThenBy(solutionPair => LinkageRouteTopologySignature
                                    .CreateStableGeometrySignature(
                                        solutionPair.Value),
                                    StringComparer.Ordinal)
                                .ThenBy(solutionPair => solutionPair.Key,
                                    StringComparer.Ordinal)
                                .Take(maximumOutputSolutionCount)
                                .Select(solutionPair => solutionPair.Key),
                            StringComparer.Ordinal);
                        foreach (var removedOutputSignature in successfulSolutions
                            .Keys
                            .Where(existingSignature =>
                                !retainedOutputTopologySignatures.Contains(
                                    existingSignature))
                            .ToList())
                        {
                            successfulSolutions.Remove(removedOutputSignature);
                            successfulOutputSourceSearchSolutions.Remove(
                                removedOutputSignature);
                            publishedCandidateSets.Remove(removedOutputSignature);
                        }

                        UpdateSearchPruningThreshold(Math.Max(1, RouteRequest.SearchOptions.MaximumSolutionsPerAlgorithm));

                        if (!successfulSolutions.ContainsKey(
                                sourceRouteTopologySignature))
                        {
                            return false;
                        }

                        publishedCompleteTopologySignatures.TryAdd(sourceRouteTopologySignature, 0);
                        publishedCompleteTopologySignatures.TryAdd(
                            LinkageRouteTopologySignature.CreateSolutionSignature(outputSolution), 0);
                        foreach (var publicationFamilySignature in publicationFamilySignatures
                            .Where(familySignature => !string.IsNullOrEmpty(familySignature)))
                        {
                            publishedRouteFamilyLineages[publicationFamilySignature] =
                                sourceRouteTopologySignature;
                            publishedRouteFamilyBestLengths[publicationFamilySignature] =
                                sourceOrdinarySearchSolution.ActualCopperLength;
                        }

                        var retainedOutputSolutionIndex = 1;
                        foreach (var retainedOutputSolution in successfulSolutions
                            .OrderBy(solutionPair =>
                                solutionPair.Value.ActualCopperLength)
                            .ThenBy(solutionPair =>
                                solutionPair.Value.GeneratedBusbarPartCount)
                            .ThenBy(solutionPair => LinkageRouteTopologySignature
                                .CreateStableGeometrySignature(solutionPair.Value),
                                StringComparer.Ordinal)
                            .ThenBy(
                                solutionPair => solutionPair.Key,
                                StringComparer.Ordinal)
                            .Select(solutionPair => solutionPair.Value))
                        {
                            retainedOutputSolution.SchemeIndex =
                                retainedOutputSolutionIndex++;
                        }

                        publishedSolutionSnapshot = CloneSolutionSnapshot(
                            outputSolution);
                    }

                    var isPostProcessedOutput = !ReferenceEquals(
                        sourceOrdinarySearchSolution,
                        outputSolution);
                    var isBendReductionOnly = isOptimizationResult &&
                        Math.Abs(sourceSolutionLength -
                            sourceOrdinarySearchSolution.ActualCopperLength) <= 1e-6d;
                    Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = AlgorithmKind,
                        ElapsedMilliseconds = SearchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref ProcessedStateCount),
                        PublishedSolution = publishedSolutionSnapshot,
                        Message = isBendReductionOnly
                            ? string.Format(CultureInfo.InvariantCulture,
                                "后台减折弯段优化完成：总长 {0:F3} mm 不变，折弯数减少为 {1}，已发布。",
                                sourceSolutionLength,
                                LinkageRouteTopologySignature.CountSolutionBends(outputSolution))
                            : isPostProcessedOutput
                            ? (isOptimizationResult
                                ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    "后台普通方案优化完成：总长由 {0:F3} mm 缩短到 {1:F3} mm，" +
                                    "终端输出后处理已同步更新。",
                                    sourceSolutionLength,
                                    sourceOrdinarySearchSolution.ActualCopperLength)
                                : "普通完整方案已完成终端输出后处理，" +
                                  "并已重算总长和实际最小绝缘距离。")
                            : isOptimizationResult
                            ? string.Format(
                                CultureInfo.InvariantCulture,
                                "后台普通方案优化完成：总长由 {0:F3} mm 缩短到 {1:F3} mm，" +
                                "普通方案已立即发布。",
                                sourceSolutionLength,
                                sourceOrdinarySearchSolution.ActualCopperLength)
                            : "发现新的普通完整路径方向拓扑；普通方案已立即发布，" +
                              "并已加入后台最短化任务。"
                    });
                }

                if (evictedFamilyLineage != null &&
                    activeSolutionOptimizationCancellationSources.TryGetValue(
                        evictedFamilyLineage, out var evictedLineageCancellationSources))
                {
                    foreach (var evictedCancellationSource in evictedLineageCancellationSources.Values)
                    {
                        try { evictedCancellationSource.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                }

                if (Volatile.Read(
                        ref optimizationPriorityAllocationActivated) != 0)
                {
                    RefreshPreferredSolutionOptimizations();
                }

                return true;
            }

            private static IReadOnlyList<KeyValuePair<string,
                IEnumerable<Point3d>>> CreateCandidateGroupCenterlines(
                    IEnumerable<LinkageRouteCandidate> routeCandidates)
            {
                return (routeCandidates ?? Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(routeCandidate => routeCandidate?.GroupRequest != null)
                    .Select(routeCandidate => new KeyValuePair<string,
                        IEnumerable<Point3d>>(
                            routeCandidate.GroupRequest.GroupKey,
                            routeCandidate.ManufacturableCenterline))
                    .ToList();
            }

            private bool RegisterSolutionOptimizationCandidate(
                LinkageRouteCandidate routeCandidate)
            {
                if (routeCandidate?.GroupRequest == null ||
                    routeCandidate.PlanarConversion?.IsValid != true)
                {
                    return false;
                }

                var groupCandidateCatalog = solutionOptimizationCandidatesByGroup
                    .GetOrAdd(
                        routeCandidate.GroupRequest.GroupKey ?? string.Empty,
                        ignoredGroupKey => new ConcurrentDictionary<string,
                            LinkageRouteCandidate>(StringComparer.Ordinal));
                return groupCandidateCatalog.TryAdd(
                    LinkageRouteCandidateFactory.GetCandidateSignature(routeCandidate),
                    routeCandidate);
            }

            public void RegisterGeneratedOptimizationCandidate(
                LinkageRouteCandidate routeCandidate)
            {
                RegisterSolutionOptimizationCandidate(routeCandidate);
            }

            private IReadOnlyList<LinkageRouteCandidate>
                TryOptimizeCompleteCandidateSetClearance(
                IReadOnlyList<LinkageRouteCandidate> seedCandidateSet,
                CancellationToken optimizationCancellationToken,
                out double sourceMinimumClearance,
                out double optimizedMinimumClearance)
            {
                var optimizedCandidateSet = seedCandidateSet.ToList();
                sourceMinimumClearance = GetCandidateSetMinimumClearance(
                    optimizedCandidateSet,
                    out var bottleneckCandidateIndices);
                optimizedMinimumClearance = sourceMinimumClearance;
                var targetClearance = Math.Max(
                    sourceMinimumClearance,
                    OutputRouteRequest.InsulationClearance);
                var optimizationStepClearance = Math.Max(
                    1e-6d,
                    OutputRouteRequest.SearchOptions.ClearanceOptimizationStep);
                var requiredOptimizationPassCount = targetClearance >
                        optimizedMinimumClearance + 1e-6d
                    ? (int)Math.Min(
                        int.MaxValue,
                        Math.Ceiling(
                            (targetClearance - optimizedMinimumClearance) /
                            optimizationStepClearance - 1e-9d))
                    : 0;
                var effectiveOptimizationPassCount = Math.Max(
                    OutputRouteRequest.SearchOptions
                        .MaximumClearanceOptimizationPassCount,
                    requiredOptimizationPassCount);
                for (var optimizationPassIndex = 0;
                    optimizationPassIndex < effectiveOptimizationPassCount &&
                    !optimizationCancellationToken.IsCancellationRequested &&
                    optimizedMinimumClearance + 1e-6d < targetClearance;
                    optimizationPassIndex++)
                {
                    var requestedStepClearance = Math.Min(
                        targetClearance,
                        optimizedMinimumClearance + OutputRouteRequest.SearchOptions
                            .ClearanceOptimizationStep);
                    var replacementResult = FindBestClearanceReplacement(
                        optimizedCandidateSet,
                        bottleneckCandidateIndices,
                        requestedStepClearance,
                        optimizedMinimumClearance,
                        optimizationCancellationToken);
                    if (replacementResult == null &&
                        requestedStepClearance - optimizedMinimumClearance > 0.5d)
                    {
                        replacementResult = FindBestClearanceReplacement(
                            optimizedCandidateSet,
                            bottleneckCandidateIndices,
                            (requestedStepClearance + optimizedMinimumClearance) /
                                2d,
                            optimizedMinimumClearance,
                            optimizationCancellationToken);
                    }

                    if (replacementResult == null)
                    {
                        break;
                    }

                    optimizedCandidateSet[replacementResult.Item1] =
                        replacementResult.Item2;
                    optimizedMinimumClearance = GetCandidateSetMinimumClearance(
                        optimizedCandidateSet,
                        out bottleneckCandidateIndices);
                }

                return optimizedMinimumClearance >
                        sourceMinimumClearance + 1e-6d
                    ? optimizedCandidateSet
                    : null;
            }

            private static bool IsCompatibleTerminalAttachmentCandidate(
                LinkageRouteCandidate sourceCandidate,
                LinkageRouteCandidate replacementCandidate)
            {
                if (sourceCandidate == null || replacementCandidate == null)
                {
                    return false;
                }

                var sourceAttachmentPrefix = GetTerminalAttachmentTopologyPrefix(
                    sourceCandidate.TopologySignature);
                if (!string.Equals(
                        sourceAttachmentPrefix,
                        "DOUBLE-START-I-BRIDGE",
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        sourceAttachmentPrefix,
                        "DOUBLE-START-L-BRIDGE",
                        StringComparison.Ordinal))
                {
                    return true;
                }

                return string.Equals(
                    sourceAttachmentPrefix,
                    GetTerminalAttachmentTopologyPrefix(
                        replacementCandidate.TopologySignature),
                    StringComparison.Ordinal);
            }

            private static string GetTerminalAttachmentTopologyPrefix(
                string topologySignature)
            {
                if (string.IsNullOrWhiteSpace(topologySignature))
                {
                    return string.Empty;
                }

                var topologySeparatorIndex = topologySignature.IndexOf('>');
                return topologySeparatorIndex < 0
                    ? topologySignature
                    : topologySignature.Substring(0, topologySeparatorIndex);
            }

            private Tuple<int, LinkageRouteCandidate> FindBestClearanceReplacement(
                IReadOnlyList<LinkageRouteCandidate> currentCandidateSet,
                ISet<int> bottleneckCandidateIndices,
                double targetClearance,
                double currentMinimumClearance,
                CancellationToken optimizationCancellationToken)
            {
                Tuple<int, LinkageRouteCandidate> bestReplacement = null;
                var bestReplacementClearance = currentMinimumClearance;
                var bestReplacementBottleneckCount =
                    bottleneckCandidateIndices.Count;
                var orderedCandidateIndices = Enumerable.Range(
                        0,
                        currentCandidateSet.Count)
                    .Where(candidateIndex =>
                        bottleneckCandidateIndices.Count == 0 ||
                        bottleneckCandidateIndices.Contains(candidateIndex))
                    .OrderBy(candidateIndex =>
                        bottleneckCandidateIndices.Contains(candidateIndex)
                            ? 0
                            : 1)
                    .ThenBy(candidateIndex => candidateIndex)
                    .ToList();
                foreach (var candidateIndex in orderedCandidateIndices)
                {
                    if (optimizationCancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var sourceCandidate = currentCandidateSet[candidateIndex];
                    var selectedOtherCandidates = currentCandidateSet
                        .Where((routeCandidate, routeCandidateIndex) =>
                            routeCandidateIndex != candidateIndex)
                        .ToList();
                    var clearanceValidationRequest = OutputRouteRequest
                        .CreateRequestedClearanceValidationRequest();
                    var replacementCandidates = new List<LinkageRouteCandidate>();
                    var sourceClearanceGroupKey = sourceCandidate.GroupRequest
                        .GetClearanceGroupKey();
                    var differentPhaseStaticSolids =
                        FixedTerminalAndObstacleSolids.Where(staticSolid =>
                            ParticipatesInInsulationClearance(staticSolid) &&
                            (string.IsNullOrWhiteSpace(
                                 sourceClearanceGroupKey) ||
                             string.IsNullOrWhiteSpace(
                                 staticSolid.ClearanceGroupKey) ||
                             !string.Equals(
                                 sourceClearanceGroupKey,
                                 staticSolid.ClearanceGroupKey,
                                 StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    var targetStaticEvaluation =
                        EvaluateCandidateSolidCollections(
                            sourceCandidate.ActualClearanceSolids,
                            differentPhaseStaticSolids,
                            "异相固定端",
                            targetClearance,
                            optimizationCancellationToken);
                    if (!targetStaticEvaluation.IsClear &&
                        targetStaticEvaluation.SuggestedSeparationDirection !=
                            null &&
                        targetStaticEvaluation.SuggestedSeparationDistance >
                            1e-6d)
                    {
                        replacementCandidates.AddRange(
                            LinkageRouteCandidateFactory
                                .BuildClearanceDirectedGeometryOptimizationCandidates(
                                    clearanceValidationRequest,
                                    sourceCandidate,
                                    FixedTerminalAndObstacleSolids,
                                    selectedOtherCandidates,
                                    targetStaticEvaluation
                                        .SuggestedSeparationDirection,
                                    targetStaticEvaluation
                                        .SuggestedSeparationDistance,
                                    OutputRouteRequest.SearchOptions
                                        .MaximumClearanceOptimizationCandidateCount,
                                    optimizationCancellationToken));
                    }

                    foreach (var selectedOtherCandidate in selectedOtherCandidates)
                    {
                        var targetPairEvaluation = EvaluateCandidatePair(
                            sourceCandidate,
                            selectedOtherCandidate,
                            targetClearance,
                            optimizationCancellationToken);
                        if (targetPairEvaluation.IsClear ||
                            targetPairEvaluation.SuggestedSeparationDirection ==
                                null ||
                            targetPairEvaluation.SuggestedSeparationDistance <=
                                1e-6d)
                        {
                            continue;
                        }

                        var localDirectedCandidates =
                            LinkageRouteCandidateFactory
                                .BuildClearanceDirectedGeometryOptimizationCandidates(
                                    clearanceValidationRequest,
                                    sourceCandidate,
                                    FixedTerminalAndObstacleSolids,
                                    selectedOtherCandidates,
                                    targetPairEvaluation
                                        .SuggestedSeparationDirection,
                                    targetPairEvaluation
                                        .SuggestedSeparationDistance,
                                    OutputRouteRequest.SearchOptions
                                        .MaximumClearanceOptimizationCandidateCount,
                                    optimizationCancellationToken);
                        replacementCandidates.AddRange(localDirectedCandidates);
                        var directedCandidatePool = LinkageRouteCandidateFactory
                            .BuildPairViolationDirectedCandidatePool(
                                clearanceValidationRequest,
                                sourceCandidate,
                                FixedTerminalAndObstacleSolids,
                                selectedOtherCandidates,
                                targetPairEvaluation
                                    .SuggestedSeparationDirection,
                                targetPairEvaluation
                                    .SuggestedSeparationDistance,
                                optimizationCancellationToken);
                        replacementCandidates.AddRange(
                            directedCandidatePool.ValidCandidates);
                        Progress?.Report(new LinkageRouteProgress
                        {
                            AlgorithmKind = AlgorithmKind,
                            ElapsedMilliseconds =
                                SearchStopwatch.ElapsedMilliseconds,
                            ProcessedStateCount = Interlocked.Read(
                                ref ProcessedStateCount),
                            Message = string.Format(
                                CultureInfo.InvariantCulture,
                                "固定请求净距几何恢复：目标 {0:F3} mm，移动组 {1}，相对组 {2}，局部控制点候选={3}，骨架位移候选={4}。",
                                targetClearance,
                                sourceCandidate.GroupRequest.GroupKey,
                                selectedOtherCandidate.GroupRequest.GroupKey,
                                localDirectedCandidates.Count,
                                directedCandidatePool.ValidCandidates.Count)
                        });
                    }

                    // 局部按瓶颈方向移动失败时，才回退到该组的完整临界路径
                    // 替换；这仍以其余种子成功候选为固定几何，不是重新执行
                    // 一轮多组全量搜索。
                    if (replacementCandidates.Count == 0)
                    {
                        var traversalDirections = new[]
                        {
                            sourceCandidate.TraversalDirection,
                            sourceCandidate.TraversalDirection ==
                                LinkageRouteTraversalDirection.Forward
                                    ? LinkageRouteTraversalDirection.Reverse
                                    : LinkageRouteTraversalDirection.Forward
                        }.Distinct().ToList();
                        foreach (var traversalDirection in traversalDirections)
                        {
                            if (optimizationCancellationToken
                                .IsCancellationRequested)
                            {
                                break;
                            }

                            using (var traversalCancellationSource =
                                CancellationTokenSource.CreateLinkedTokenSource(
                                    optimizationCancellationToken))
                            {
                                traversalCancellationSource.CancelAfter(
                                    OutputRouteRequest.SearchOptions
                                        .ClearanceOptimizationTraversalLimitMilliseconds);
                                try
                                {
                                    replacementCandidates.AddRange(
                                        LinkageRouteCandidateFactory
                                            .BuildClearanceDrivenCandidates(
                                                clearanceValidationRequest,
                                                sourceCandidate.GroupRequest,
                                                FixedTerminalAndObstacleSolids,
                                                selectedOtherCandidates,
                                                traversalDirection,
                                                traversalCancellationSource.Token));
                                }
                                catch (OperationCanceledException) when (
                                    traversalCancellationSource
                                        .IsCancellationRequested)
                                {
                                }
                            }
                        }
                    }

                    if (solutionOptimizationCandidatesByGroup.TryGetValue(
                            sourceCandidate.GroupRequest.GroupKey ?? string.Empty,
                            out var registeredCandidates))
                    {
                        replacementCandidates.AddRange(
                            registeredCandidates.Values.Where(
                                registeredCandidate =>
                                    IsCompatibleTerminalAttachmentCandidate(
                                        sourceCandidate,
                                        registeredCandidate)));
                    }

                    foreach (var replacementCandidate in replacementCandidates
                        .Where(routeCandidate =>
                            routeCandidate?.PlanarConversion?.IsValid == true &&
                            HasCompletePhysicalSectionIdentity(routeCandidate))
                        .GroupBy(LinkageRouteCandidateFactory
                            .GetCandidateSignature)
                        .Select(candidateGroup => candidateGroup.First())
                        .Take(OutputRouteRequest.SearchOptions
                            .MaximumClearanceOptimizationCandidateCount))
                    {
                        if (optimizationCancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var replacementCandidateSet = currentCandidateSet.ToList();
                        replacementCandidateSet[candidateIndex] =
                            replacementCandidate;
                        var replacementClearance =
                            GetCandidateSetMinimumClearance(
                                replacementCandidateSet,
                                out var replacementBottleneckCandidateIndices);
                        var improvesMinimumClearance = replacementClearance >
                            bestReplacementClearance + 1e-6d;
                        var preservesClearanceAndReducesBottlenecks =
                            Math.Abs(
                                replacementClearance -
                                bestReplacementClearance) <= 1e-6d &&
                            replacementBottleneckCandidateIndices.Count <
                                bestReplacementBottleneckCount;
                        if (!improvesMinimumClearance &&
                            !preservesClearanceAndReducesBottlenecks)
                        {
                            continue;
                        }

                        bestReplacementClearance = replacementClearance;
                        bestReplacementBottleneckCount =
                            replacementBottleneckCandidateIndices.Count;
                        bestReplacement = Tuple.Create(
                            candidateIndex,
                            replacementCandidate);
                    }

                    if (bestReplacementClearance + 1e-6d >= targetClearance)
                    {
                        break;
                    }
                }

                return bestReplacement;
            }

            private bool HasCompletePhysicalSectionIdentity(
                LinkageRouteCandidate routeCandidate)
            {
                if (routeCandidate?.GroupRequest == null ||
                    routeCandidate.PlanarConversion?.StackMembers == null ||
                    OutputRouteRequest?.Groups == null)
                {
                    return true;
                }

                var outputGroupRequest = OutputRouteRequest.Groups.FirstOrDefault(
                    routeGroupRequest => string.Equals(
                        routeGroupRequest?.GroupKey,
                        routeCandidate.GroupRequest.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (outputGroupRequest == null ||
                    !outputGroupRequest.HasSectionSpecificBusbarSettings(
                        OutputRouteRequest))
                {
                    return true;
                }

                var physicalSectionCount = routeCandidate.PlanarConversion
                    .CenterlineParts?.Count ?? 0;
                if (!routeCandidate.PlanarConversion
                        .UsesSectionSpecificBusbarGeometry ||
                    physicalSectionCount <= 0)
                {
                    return false;
                }

                var physicalSettings = routeCandidate.PlanarConversion
                    .StackMembers
                    .Where(stackMember => stackMember?.PlanarParts != null)
                    .SelectMany(stackMember => stackMember.PlanarParts)
                    .Where(planarSetting => planarSetting != null)
                    .ToList();
                return Enumerable.Range(0, physicalSectionCount).All(
                    physicalSectionIndex => HasExpectedPhysicalSection(
                        OutputRouteRequest,
                        outputGroupRequest,
                        physicalSectionCount,
                        physicalSectionIndex,
                        physicalSettings));
            }

            private double GetCandidateSetMinimumClearance(
                IReadOnlyList<LinkageRouteCandidate> candidateSet,
                out ISet<int> bottleneckCandidateIndices)
            {
                var minimumClearance = double.PositiveInfinity;
                var minimumClearanceSources = new List<Tuple<int, int>>();
                for (var candidateIndex = 0;
                    candidateIndex < candidateSet.Count;
                    candidateIndex++)
                {
                    var staticMinimumClearance = GetExactStaticMinimumDistance(
                        candidateSet[candidateIndex]);
                    RegisterMinimumClearanceSource(
                        staticMinimumClearance,
                        candidateIndex,
                        -1,
                        ref minimumClearance,
                        minimumClearanceSources);
                    for (var otherCandidateIndex = candidateIndex + 1;
                        otherCandidateIndex < candidateSet.Count;
                        otherCandidateIndex++)
                    {
                        RegisterMinimumClearanceSource(
                            GetExactPairMinimumDistance(
                                candidateSet[candidateIndex],
                                candidateSet[otherCandidateIndex]),
                            candidateIndex,
                            otherCandidateIndex,
                            ref minimumClearance,
                            minimumClearanceSources);
                    }
                }

                bottleneckCandidateIndices = new HashSet<int>(
                    minimumClearanceSources.SelectMany(minimumClearanceSource =>
                        minimumClearanceSource.Item2 < 0
                            ? new[] { minimumClearanceSource.Item1 }
                            : new[]
                            {
                                minimumClearanceSource.Item1,
                                minimumClearanceSource.Item2
                            }));
                return minimumClearance;
            }

            private static void RegisterMinimumClearanceSource(
                double candidateClearance,
                int firstCandidateIndex,
                int secondCandidateIndex,
                ref double minimumClearance,
                IList<Tuple<int, int>> minimumClearanceSources)
            {
                if (candidateClearance + 1e-6d < minimumClearance)
                {
                    minimumClearance = candidateClearance;
                    minimumClearanceSources.Clear();
                    minimumClearanceSources.Add(Tuple.Create(
                        firstCandidateIndex,
                        secondCandidateIndex));
                }
                else if (Math.Abs(candidateClearance - minimumClearance) <= 1e-6d)
                {
                    minimumClearanceSources.Add(Tuple.Create(
                        firstCandidateIndex,
                        secondCandidateIndex));
                }
            }

            private void ScheduleSolutionOptimization(
                string solutionSignature,
                string solutionTopologySignature,
                IReadOnlyList<LinkageRouteCandidate> selectedCandidateList)
            {
                if (CancellationToken.IsCancellationRequested ||
                    Volatile.Read(ref acceptsSolutionOptimizationTasks) == 0 ||
                    selectedCandidateList == null ||
                    selectedCandidateList.Count == 0)
                {
                    return;
                }

                var normalizedSolutionTopologySignature =
                    solutionTopologySignature ?? string.Empty;
                if (!IsPreferredSolutionOptimizationTopology(
                        normalizedSolutionTopologySignature))
                {
                    return;
                }

                var solutionOptimizationStartMilliseconds =
                    solutionOptimizationLineageStartMilliseconds.GetOrAdd(
                        normalizedSolutionTopologySignature,
                        SearchStopwatch.ElapsedMilliseconds);
                var remainingSolutionOptimizationMilliseconds = RouteRequest
                    .SearchOptions.MaximumSolutionOptimizationDurationMilliseconds -
                    (SearchStopwatch.ElapsedMilliseconds -
                        solutionOptimizationStartMilliseconds);
                if (remainingSolutionOptimizationMilliseconds <= 0L)
                {
                    return;
                }

                if (!scheduledSolutionOptimizationSignatures.TryAdd(
                    solutionSignature ??
                        GetCandidateSetSignature(selectedCandidateList),
                    0))
                {
                    return;
                }

                var optimizationSeedCandidates = selectedCandidateList.ToList();
                var optimizationCandidateSetSignature =
                    GetCandidateSetSignature(optimizationSeedCandidates);
                var optimizationCandidatePoolSnapshot = Volatile.Read(
                    ref solutionOptimizationCandidatePools);
                var optimizationCancellationSource =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        CancellationToken);
                optimizationCancellationSource.CancelAfter(
                    (int)Math.Max(
                        1L,
                        Math.Min(
                            int.MaxValue,
                            remainingSolutionOptimizationMilliseconds)));
                var topologyOptimizationCancellationSources =
                    activeSolutionOptimizationCancellationSources.GetOrAdd(
                        normalizedSolutionTopologySignature,
                        ignoredTopologySignature => new ConcurrentDictionary<
                            string,
                            CancellationTokenSource>(StringComparer.Ordinal));
                if (!topologyOptimizationCancellationSources.TryAdd(
                        optimizationCandidateSetSignature,
                        optimizationCancellationSource))
                {
                    optimizationCancellationSource.Dispose();
                    return;
                }
                var optimizationCancellationToken =
                    optimizationCancellationSource.Token;
                if (!TryRegisterBackgroundTask(false))
                {
                    topologyOptimizationCancellationSources.TryRemove(optimizationCandidateSetSignature, out _);
                    optimizationCancellationSource.Dispose();
                    return;
                }
                try
                {
                    Task.Run(async () =>
                    {
                        var enteredDeepOptimizationSemaphore = false;
                        var enteredOptimizationSemaphore = false;
                        try
                        {
                            await solutionOptimizationSemaphore
                                .WaitAsync(optimizationCancellationToken)
                                .ConfigureAwait(false);
                            enteredOptimizationSemaphore = true;
                            if (optimizationCancellationToken
                                .IsCancellationRequested)
                            {
                                return;
                            }

                            if (!IsCurrentSolutionOptimizationSeed(
                                    solutionTopologySignature,
                                    optimizationSeedCandidates))
                            {
                                return;
                            }

                            RegisterSolutionOptimizationCandidatePools(
                                optimizationCandidatePoolSnapshot);
                            foreach (var optimizationSeedCandidate in
                                optimizationSeedCandidates)
                            {
                                RegisterSolutionOptimizationCandidate(
                                    optimizationSeedCandidate);
                            }

                            var sourceCandidateSetLength =
                                optimizationSeedCandidates.Sum(routeCandidate =>
                                    routeCandidate.PlanarConversion
                                        .ActualCopperLength);
                            var optimizedCandidateSet =
                                TryOptimizeCompleteCandidateSet(
                                    optimizationSeedCandidates,
                                    false,
                                    optimizationCancellationToken);
                            solutionOptimizationSemaphore.Release();
                            enteredOptimizationSemaphore = false;
                            if (IsImprovedCandidateSet(
                                    optimizedCandidateSet,
                                    optimizationSeedCandidates))
                            {
                                TryAddCompleteSolution(
                                    optimizedCandidateSet,
                                    true,
                                    sourceCandidateSetLength,
                                    solutionTopologySignature);
                                return;
                            }

                            if (optimizationCancellationToken
                                    .IsCancellationRequested ||
                                !IsCurrentSolutionOptimizationSeed(
                                    solutionTopologySignature,
                                    optimizationSeedCandidates))
                            {
                                return;
                            }

                            // 深度几何和净距优化共享限流闸门；该闸门最多占用总
                            // 优化并发减一路，从而保证快速直接几何优化不会排在
                            // 长耗时任务之后。
                            await deepSolutionOptimizationSemaphore
                                .WaitAsync(optimizationCancellationToken)
                                .ConfigureAwait(false);
                            enteredDeepOptimizationSemaphore = true;
                            await solutionOptimizationSemaphore
                                .WaitAsync(optimizationCancellationToken)
                                .ConfigureAwait(false);
                            enteredOptimizationSemaphore = true;
                            if (optimizationCancellationToken
                                    .IsCancellationRequested ||
                                !IsCurrentSolutionOptimizationSeed(
                                    solutionTopologySignature,
                                    optimizationSeedCandidates))
                            {
                                return;
                            }

                            optimizedCandidateSet =
                                TryOptimizeCompleteCandidateSet(
                                    optimizationSeedCandidates,
                                    true,
                                    optimizationCancellationToken);
                            if (IsImprovedCandidateSet(
                                    optimizedCandidateSet,
                                    optimizationSeedCandidates))
                            {
                                TryAddCompleteSolution(
                                    optimizedCandidateSet,
                                    true,
                                    sourceCandidateSetLength,
                                    solutionTopologySignature);
                            }
                        }
                        catch (OperationCanceledException) when (
                            optimizationCancellationToken.IsCancellationRequested)
                        {
                        }
                        catch (Exception optimizationException)
                        {
                            Progress?.Report(new LinkageRouteProgress
                            {
                                AlgorithmKind = AlgorithmKind,
                                ElapsedMilliseconds =
                                    SearchStopwatch.ElapsedMilliseconds,
                                ProcessedStateCount = Interlocked.Read(
                                    ref ProcessedStateCount),
                                Message = "方案后台最短化任务失败，原成功方案继续保留：" +
                                    optimizationException.Message
                            });
                        }
                        finally
                        {
                            if (enteredOptimizationSemaphore)
                            {
                                solutionOptimizationSemaphore.Release();
                            }

                            if (enteredDeepOptimizationSemaphore)
                            {
                                deepSolutionOptimizationSemaphore.Release();
                            }

                            topologyOptimizationCancellationSources.TryRemove(
                                optimizationCandidateSetSignature,
                                out _);

                            optimizationCancellationSource.Dispose();

                            FinishBackgroundTask(false);
                        }
                    });
                }
                catch (Exception schedulingException)
                {
                    topologyOptimizationCancellationSources.TryRemove(
                        optimizationCandidateSetSignature,
                        out _);

                    optimizationCancellationSource.Dispose();
                    FinishBackgroundTask(false);

                    Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = AlgorithmKind,
                        ElapsedMilliseconds = SearchStopwatch.ElapsedMilliseconds,
                        ProcessedStateCount = Interlocked.Read(
                            ref ProcessedStateCount),
                        Message = "无法创建方案后台最短化任务，原成功方案继续保留：" +
                            schedulingException.Message
                    });
                }
            }

            private HashSet<string>
                GetPreferredSolutionOptimizationTopologySignaturesLocked()
            {
                if (Volatile.Read(
                        ref optimizationPriorityAllocationActivated) != 0)
                {
                    return new HashSet<string>(
                        successfulSolutions
                            .OrderBy(solutionPair =>
                                solutionPair.Value.ActualCopperLength)
                            .ThenBy(solutionPair =>
                                solutionPair.Value.GeneratedBusbarPartCount)
                            .ThenBy(solutionPair => LinkageRouteTopologySignature
                                .CreateStableGeometrySignature(solutionPair.Value),
                                StringComparer.Ordinal)
                            .ThenBy(solutionPair => solutionPair.Key,
                                StringComparer.Ordinal)
                            .Take(PrioritizedOptimizationSolutionCount)
                            .Select(solutionPair => solutionPair.Key),
                        StringComparer.Ordinal);
                }

                if (IsExplorationWindowActive)
                {
                    // 探索期只优化当前最短的方案；出现新的最短方案时，排出前列者的
                    // 优化任务由 RefreshPreferredSolutionOptimizations 取消并让出线程。
                    return new HashSet<string>(
                        successfulSolutions
                            .OrderBy(solutionPair => solutionPair.Value.ActualCopperLength)
                            .ThenBy(solutionPair => solutionPair.Value.GeneratedBusbarPartCount)
                            .ThenBy(solutionPair => LinkageRouteTopologySignature
                                .CountSolutionBends(solutionPair.Value))
                            .ThenBy(solutionPair => solutionPair.Key, StringComparer.Ordinal)
                            .Take(ExplorationOptimizedSolutionCount)
                            .Select(solutionPair => solutionPair.Key),
                        StringComparer.Ordinal);
                }

                return new HashSet<string>(
                    SelectPreferredSolutionOptimizationTopologySignatures(
                        successfulSolutions,
                        RouteRequest.SearchOptions
                            .PreferredSolutionOptimizationCount),
                    StringComparer.Ordinal);
            }

            private static IReadOnlyList<string>
                SelectPreferredSolutionOptimizationTopologySignatures(
                IEnumerable<KeyValuePair<string, LinkageRouteSolution>>
                        successfulSearchSolutionPairs,
                int preferredSolutionOptimizationCount)
            {
                var orderedSolutionPairs = (successfulSearchSolutionPairs ??
                        Enumerable.Empty<KeyValuePair<string,
                            LinkageRouteSolution>>())
                    .OrderBy(solutionPair => solutionPair.Value.ActualCopperLength)
                    .ThenBy(solutionPair =>
                        solutionPair.Value.GeneratedBusbarPartCount)
                    .ThenBy(solutionPair => LinkageRouteTopologySignature
                        .CreateStableGeometrySignature(solutionPair.Value),
                        StringComparer.Ordinal)
                    .ThenBy(solutionPair => solutionPair.Key,
                        StringComparer.Ordinal)
                    .ToList();
                var perCategoryCount = Math.Max(
                    1,
                    preferredSolutionOptimizationCount);
                var preferredOptimizedSolutionPairs = orderedSolutionPairs
                    .Where(solutionPair =>
                        !string.IsNullOrWhiteSpace(solutionPair.Value
                            .OptimizationSourceTopologySignature))
                    .Take(perCategoryCount)
                    .ToList();
                var preferredOrdinarySolutionPairs = orderedSolutionPairs
                    .Where(solutionPair =>
                        string.IsNullOrWhiteSpace(solutionPair.Value
                            .OptimizationSourceTopologySignature))
                    .Take(perCategoryCount)
                    .ToList();
                return preferredOptimizedSolutionPairs
                    .Concat(preferredOrdinarySolutionPairs)
                    .OrderBy(solutionPair => solutionPair.Value.ActualCopperLength)
                    .ThenBy(solutionPair =>
                        solutionPair.Value.GeneratedBusbarPartCount)
                    .ThenBy(solutionPair => LinkageRouteTopologySignature
                        .CreateStableGeometrySignature(solutionPair.Value),
                        StringComparer.Ordinal)
                    .ThenBy(solutionPair => solutionPair.Key,
                        StringComparer.Ordinal)
                    .Select(solutionPair => solutionPair.Key)
                    .ToList();
            }

            private bool UsesPreferredSolutionOptimizationSet =>
                Volatile.Read(ref postSwitchSolutionAllocationActivated) != 0 ||
                IsExplorationWindowActive;

            private bool IsPreferredSolutionOptimizationTopology(
                string solutionTopologySignature)
            {
                if (!UsesPreferredSolutionOptimizationSet)
                {
                    return true;
                }

                lock (resultGate)
                {
                    return GetPreferredSolutionOptimizationTopologySignaturesLocked()
                        .Contains(solutionTopologySignature ?? string.Empty);
                }
            }

            private void RefreshPreferredSolutionOptimizations()
            {
                if (!UsesPreferredSolutionOptimizationSet)
                {
                    return;
                }

                HashSet<string> preferredTopologySignatures;
                lock (resultGate)
                {
                    preferredTopologySignatures =
                        GetPreferredSolutionOptimizationTopologySignaturesLocked();
                }

                foreach (var activeOptimizationCancellationSource in
                    activeSolutionOptimizationCancellationSources)
                {
                    if (!preferredTopologySignatures.Contains(
                            activeOptimizationCancellationSource.Key))
                    {
                        foreach (var topologyCancellationSource in
                            activeOptimizationCancellationSource.Value.Values)
                        {
                            topologyCancellationSource.Cancel();
                        }
                    }
                }

                SchedulePublishedSolutionReoptimizations();
            }

            private static bool IsShorterSolutionOptimizationResult(
                IReadOnlyList<LinkageRouteCandidate> optimizedCandidateSet,
                double sourceCandidateSetLength)
            {
                return optimizedCandidateSet != null &&
                    optimizedCandidateSet.Sum(routeCandidate =>
                        routeCandidate.PlanarConversion.ActualCopperLength) +
                        1e-8d < sourceCandidateSetLength;
            }

            private bool IsCurrentSolutionOptimizationSeed(
                string solutionTopologySignature,
                IReadOnlyList<LinkageRouteCandidate> optimizationSeedCandidates)
            {
                if (optimizationSeedCandidates == null ||
                    optimizationSeedCandidates.Count == 0)
                {
                    return false;
                }

                lock (resultGate)
                {
                    if (UsesPreferredSolutionOptimizationSet &&
                        !GetPreferredSolutionOptimizationTopologySignaturesLocked()
                            .Contains(
                                solutionTopologySignature ?? string.Empty))
                    {
                        return false;
                    }

                    return publishedCandidateSets.TryGetValue(
                            solutionTopologySignature ?? string.Empty,
                            out var currentTopologyCandidateSet) &&
                        string.Equals(
                            GetCandidateSetSignature(currentTopologyCandidateSet),
                            GetCandidateSetSignature(
                                optimizationSeedCandidates),
                            StringComparison.Ordinal);
                }
            }

            private IReadOnlyList<LinkageRouteCandidate>
                TryOptimizeCompleteCandidateSet(
                    IReadOnlyList<LinkageRouteCandidate> sourceCandidateSet,
                    bool includeDeepOptimizationCandidates,
                    CancellationToken optimizationCancellationToken)
            {
                if (optimizationCancellationToken.IsCancellationRequested ||
                    sourceCandidateSet == null ||
                    sourceCandidateSet.Count == 0)
                {
                    return null;
                }

                var optimizedCandidateSet = sourceCandidateSet.ToList();
                var sourceCandidateSetLength = optimizedCandidateSet.Sum(
                    routeCandidate => routeCandidate.PlanarConversion
                        .ActualCopperLength);
                var candidateIndicesByDescendingLength = Enumerable.Range(
                        0,
                        optimizedCandidateSet.Count)
                    .OrderByDescending(candidateIndex =>
                        optimizedCandidateSet[candidateIndex]
                            .PlanarConversion.ActualCopperLength)
                    .ThenBy(candidateIndex =>
                        optimizedCandidateSet[candidateIndex]
                            .GroupRequest.GroupKey,
                        StringComparer.Ordinal)
                    .ToList();
                var optimizationCandidateTier =
                    includeDeepOptimizationCandidates ? 1 : 0;
                var hasFastReplacement = false;
            RestartFastOptimizationScan:
                if (!optimizationCancellationToken.IsCancellationRequested)
                {
                    var replacedFastCandidateInTier = false;
                    candidateIndicesByDescendingLength = Enumerable.Range(
                            0,
                            optimizedCandidateSet.Count)
                        .OrderByDescending(candidateIndex =>
                            optimizedCandidateSet[candidateIndex]
                                .PlanarConversion.ActualCopperLength)
                        .ThenBy(candidateIndex =>
                            optimizedCandidateSet[candidateIndex]
                                .GroupRequest.GroupKey,
                            StringComparer.Ordinal)
                        .ToList();
                    for (var candidateLengthRank = 0;
                        candidateLengthRank < candidateIndicesByDescendingLength.Count;
                        candidateLengthRank++)
                    {
                        if (optimizationCancellationToken.IsCancellationRequested)
                        {
                            return null;
                        }

                        var candidateIndex = candidateIndicesByDescendingLength[
                            candidateLengthRank];
                        var currentRouteCandidate = optimizedCandidateSet[
                            candidateIndex];
                        var selectedOtherRouteCandidates = optimizedCandidateSet
                            .Where((routeCandidate, otherCandidateIndex) =>
                                otherCandidateIndex != candidateIndex)
                            .ToList();
                        var shorterCandidateNeighborhood =
                            GetShorterSolutionOptimizationCandidates(
                                currentRouteCandidate,
                                selectedOtherRouteCandidates,
                                optimizationCandidateTier > 0,
                                optimizationCancellationToken);
                        foreach (var shorterRouteCandidate in
                            shorterCandidateNeighborhood)
                        {
                            if (optimizationCancellationToken
                                .IsCancellationRequested)
                            {
                                return null;
                            }

                            var currentCandidateSetLength = optimizedCandidateSet
                                .Sum(routeCandidate => routeCandidate
                                    .PlanarConversion.ActualCopperLength);
                            var replacementSetLength = currentCandidateSetLength -
                                currentRouteCandidate.PlanarConversion
                                    .ActualCopperLength +
                                shorterRouteCandidate.PlanarConversion
                                    .ActualCopperLength;
                            if (replacementSetLength + 1e-8d >=
                                    currentCandidateSetLength &&
                                !LinkageRouteCandidateFactory.IsImprovedOptimizationCandidate(
                                    shorterRouteCandidate, currentRouteCandidate))
                            {
                                continue;
                            }

                            Interlocked.Increment(ref ProcessedStateCount);
                            var replacementClearanceEvaluation =
                                EvaluateAgainstSelected(
                                    shorterRouteCandidate,
                                    selectedOtherRouteCandidates,
                                    optimizationCancellationToken);
                            if (!replacementClearanceEvaluation.IsClear)
                            {
                                continue;
                            }

                            if (optimizationCandidateTier == 0)
                            {
                                // 快速层继续横扫其余逻辑组，把彼此兼容的直接
                                // 优化合并为一次发布，避免多组水平搭接串行排队。
                                optimizedCandidateSet[candidateIndex] =
                                    shorterRouteCandidate;
                                replacedFastCandidateInTier = true;
                                hasFastReplacement = true;
                                RegisterSolutionOptimizationCandidate(
                                    shorterRouteCandidate);
                                break;
                            }

                            // 深层候选仍保持单替换立即发布，避免一次任务长时间
                            // 占用后台并发槽。
                            optimizedCandidateSet[candidateIndex] =
                                shorterRouteCandidate;
                            hasFastReplacement = true;
                            RegisterSolutionOptimizationCandidate(
                                shorterRouteCandidate);
                            replacedFastCandidateInTier = true;
                            break;
                        }
                        if (replacedFastCandidateInTier)
                        {
                            break;
                        }
                    }

                    if (replacedFastCandidateInTier)
                    {
                        goto RestartFastOptimizationScan;
                    }
                }

                if (!includeDeepOptimizationCandidates)
                {
                    return hasFastReplacement ? optimizedCandidateSet : null;
                }

                var hasDeepReplacement = false;
                while (!optimizationCancellationToken.IsCancellationRequested)
                {
                    var jointlyOptimizedCandidateSet =
                        TryOptimizeCandidatePair(
                            optimizedCandidateSet,
                            optimizationCancellationToken);
                    if (jointlyOptimizedCandidateSet == null ||
                        jointlyOptimizedCandidateSet.Sum(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength) +
                        1e-8d >= optimizedCandidateSet.Sum(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength))
                    {
                        break;
                    }

                    optimizedCandidateSet = jointlyOptimizedCandidateSet.ToList();
                    hasDeepReplacement = true;
                    foreach (var deepOptimizedCandidate in optimizedCandidateSet)
                    {
                        RegisterSolutionOptimizationCandidate(
                            deepOptimizedCandidate);
                    }
                }

                return (hasFastReplacement || hasDeepReplacement) &&
                    IsImprovedCandidateSet(optimizedCandidateSet, sourceCandidateSet)
                        ? optimizedCandidateSet
                        : null;
            }

            private static int CountCandidateSetBends(
                IEnumerable<LinkageRouteCandidate> candidateSet)
            {
                return (candidateSet ?? Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(routeCandidate => routeCandidate != null)
                    .Sum(routeCandidate => LinkageRouteTopologySignature
                        .CountCenterlineBends(routeCandidate.ManufacturableCenterline));
            }

            // 整套优化结果：更短，或总长相同而折弯更少（减折弯段优化）。
            private static bool IsImprovedCandidateSet(
                IReadOnlyList<LinkageRouteCandidate> optimizedCandidateSet,
                IReadOnlyList<LinkageRouteCandidate> sourceCandidateSet)
            {
                if (optimizedCandidateSet == null || sourceCandidateSet == null)
                    return false;
                if (IsShorterSolutionOptimizationResult(optimizedCandidateSet,
                        sourceCandidateSet.Sum(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength)))
                    return true;
                return LinkageRouteCandidateFactory.IsImprovedOptimizationLength(
                        optimizedCandidateSet.Sum(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength),
                        CountCandidateSetBends(optimizedCandidateSet),
                        sourceCandidateSet.Sum(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength),
                        CountCandidateSetBends(sourceCandidateSet));
            }

            private IReadOnlyList<LinkageRouteCandidate> TryOptimizeCandidatePair(
                IReadOnlyList<LinkageRouteCandidate> sourceCandidateSet,
                CancellationToken optimizationCancellationToken)
            {
                if (optimizationCancellationToken.IsCancellationRequested ||
                    sourceCandidateSet == null ||
                    sourceCandidateSet.Count < 2)
                {
                    return null;
                }

                var sourceCandidateSetLength = sourceCandidateSet.Sum(
                    routeCandidate => routeCandidate.PlanarConversion
                        .ActualCopperLength);
                var bestCandidateSetLength = sourceCandidateSetLength;
                List<LinkageRouteCandidate> bestCandidateSet = null;
                for (var firstCandidateIndex = 0;
                    firstCandidateIndex < sourceCandidateSet.Count - 1;
                    firstCandidateIndex++)
                {
                    var firstSourceCandidate = sourceCandidateSet[
                        firstCandidateIndex];
                    for (var secondCandidateIndex = firstCandidateIndex + 1;
                        secondCandidateIndex < sourceCandidateSet.Count;
                        secondCandidateIndex++)
                    {
                        if (optimizationCancellationToken.IsCancellationRequested)
                        {
                            return null;
                        }

                        var secondSourceCandidate = sourceCandidateSet[
                            secondCandidateIndex];
                        var unchangedCandidates = sourceCandidateSet
                            .Where((routeCandidate, candidateIndex) =>
                                candidateIndex != firstCandidateIndex &&
                                candidateIndex != secondCandidateIndex)
                            .ToList();
                        var firstShorterCandidates =
                            GetJointShorterTopologyCandidates(
                                firstSourceCandidate,
                                unchangedCandidates,
                                32,
                                optimizationCancellationToken);
                        if (firstShorterCandidates.Count == 0)
                        {
                            continue;
                        }

                        var secondShorterCandidates =
                            GetJointShorterTopologyCandidates(
                                secondSourceCandidate,
                                unchangedCandidates,
                                32,
                                optimizationCancellationToken);
                        if (secondShorterCandidates.Count == 0)
                        {
                            continue;
                        }

                        foreach (var firstReplacementCandidate in
                            firstShorterCandidates)
                        {
                            if (optimizationCancellationToken
                                .IsCancellationRequested)
                            {
                                return null;
                            }

                            var firstReplacementSetLength =
                                sourceCandidateSetLength -
                                firstSourceCandidate.PlanarConversion
                                    .ActualCopperLength +
                                firstReplacementCandidate.PlanarConversion
                                    .ActualCopperLength;
                            if (firstReplacementSetLength -
                                    secondSourceCandidate.PlanarConversion
                                        .ActualCopperLength +
                                    secondShorterCandidates[0].PlanarConversion
                                        .ActualCopperLength + 1e-8d >=
                                bestCandidateSetLength)
                            {
                                continue;
                            }

                            Interlocked.Increment(ref ProcessedStateCount);
                            if (!EvaluateAgainstSelected(
                                    firstReplacementCandidate,
                                    unchangedCandidates,
                                    optimizationCancellationToken)
                                .IsClear)
                            {
                                continue;
                            }

                            foreach (var secondReplacementCandidate in
                                secondShorterCandidates)
                            {
                                var replacementSetLength =
                                    firstReplacementSetLength -
                                    secondSourceCandidate.PlanarConversion
                                        .ActualCopperLength +
                                    secondReplacementCandidate.PlanarConversion
                                        .ActualCopperLength;
                                if (replacementSetLength + 1e-8d >=
                                    bestCandidateSetLength)
                                {
                                    continue;
                                }

                                Interlocked.Increment(ref ProcessedStateCount);
                                if (!EvaluateAgainstSelected(
                                        secondReplacementCandidate,
                                        unchangedCandidates.Concat(new[]
                                        {
                                            firstReplacementCandidate
                                        }),
                                        optimizationCancellationToken)
                                    .IsClear)
                                {
                                    continue;
                                }

                                bestCandidateSet = sourceCandidateSet.ToList();
                                bestCandidateSet[firstCandidateIndex] =
                                    firstReplacementCandidate;
                                bestCandidateSet[secondCandidateIndex] =
                                    secondReplacementCandidate;
                                bestCandidateSetLength = replacementSetLength;
                            }
                        }
                    }
                }

                return bestCandidateSet;
            }

            private IReadOnlyList<LinkageRouteCandidate>
                GetJointShorterTopologyCandidates(
                    LinkageRouteCandidate sourceRouteCandidate,
                    IReadOnlyList<LinkageRouteCandidate> unchangedCandidates,
                    int maximumCandidateCount,
                    CancellationToken optimizationCancellationToken)
            {
                var jointShorterCandidates =
                    GetCatalogShorterTopologyCandidates(
                            sourceRouteCandidate,
                            maximumCandidateCount)
                        .ToList();
                if (optimizationCancellationToken.IsCancellationRequested)
                {
                    return jointShorterCandidates;
                }

                // 成对优化时不能用尚未移动的另一根排提前淘汰局部候选。这里只对
                // 固定实体和其余未变化排建候选，最后在 TryOptimizeCandidatePair 中
                // 把两根替换排放回同一方案，统一复核彼此净距和完整方案合法性。
                var jointOptimizationCandidateCacheKey =
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        sourceRouteCandidate) +
                    "||JOINT||UNCHANGED=" +
                    GetCandidateSetSignature(unchangedCandidates);
                var directJointOptimizationCandidates =
                    solutionOptimizationCandidateCache.GetOrAdd(
                        jointOptimizationCandidateCacheKey,
                        ignoredCacheKey => new Lazy<IReadOnlyList<
                            LinkageRouteCandidate>>(
                            () => LinkageRouteCandidateFactory
                                .BuildDirectGeometryOptimizationCandidates(
                                    RouteRequest,
                                    sourceRouteCandidate,
                                    FixedTerminalAndObstacleSolids,
                                    unchangedCandidates,
                                    Math.Max(
                                        1,
                                        Math.Min(
                                            16,
                                            RouteRequest.SearchOptions
                                                .MaximumClearanceRepairCandidatesPerState)),
                                    optimizationCancellationToken),
                            LazyThreadSafetyMode.ExecutionAndPublication))
                    .Value;
                foreach (var directJointOptimizationCandidate in
                    directJointOptimizationCandidates)
                {
                    RegisterSolutionOptimizationCandidate(
                        directJointOptimizationCandidate);
                    jointShorterCandidates.Add(
                        directJointOptimizationCandidate);
                }

                return OrderShorterOptimizationCandidates(
                    jointShorterCandidates,
                    sourceRouteCandidate,
                    maximumCandidateCount);
            }

            private IReadOnlyList<LinkageRouteCandidate>
                GetCatalogShorterTopologyCandidates(
                    LinkageRouteCandidate sourceRouteCandidate,
                    int maximumCandidateCount)
            {
                if (sourceRouteCandidate?.GroupRequest == null ||
                    !solutionOptimizationCandidatesByGroup.TryGetValue(
                        sourceRouteCandidate.GroupRequest.GroupKey ?? string.Empty,
                        out var groupCandidateCatalog))
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                var sourceRouteLength = sourceRouteCandidate.PlanarConversion
                    .ActualCopperLength;
                return groupCandidateCatalog.Values
                    .Where(routeCandidate =>
                        routeCandidate?.PlanarConversion?.IsValid == true &&
                        routeCandidate.PlanarConversion.ActualCopperLength +
                            1e-8d < sourceRouteLength)
                    .GroupBy(LinkageRouteCandidateFactory.GetCandidateSignature)
                    .Select(candidateGroup => candidateGroup
                        .OrderBy(routeCandidate => routeCandidate.PlanarConversion
                            .ActualCopperLength)
                        .ThenBy(routeCandidate =>
                            routeCandidate.GeneratedBusbarPartCount)
                        .First())
                    .OrderBy(routeCandidate => routeCandidate.PlanarConversion
                        .ActualCopperLength)
                    .ThenBy(routeCandidate =>
                        routeCandidate.GeneratedBusbarPartCount)
                    .ThenBy(
                        LinkageRouteCandidateFactory.GetCandidateSignature,
                        StringComparer.Ordinal)
                    .Take(Math.Max(1, maximumCandidateCount))
                    .ToList();
            }

            private IEnumerable<LinkageRouteCandidate>
                GetShorterSolutionOptimizationCandidates(
                    LinkageRouteCandidate sourceRouteCandidate,
                    IReadOnlyList<LinkageRouteCandidate>
                        selectedOtherRouteCandidates,
                    bool includeDeepOptimizationCandidates,
                    CancellationToken optimizationCancellationToken)
            {
                var sourceRouteCandidateLength = sourceRouteCandidate
                    .PlanarConversion.ActualCopperLength;
                var shorterCandidateNeighborhood =
                    new List<LinkageRouteCandidate>();
                if (includeDeepOptimizationCandidates &&
                    solutionOptimizationCandidatesByGroup.TryGetValue(
                        sourceRouteCandidate.GroupRequest.GroupKey ?? string.Empty,
                        out var groupCandidateCatalog))
                {
                    shorterCandidateNeighborhood.AddRange(
                        groupCandidateCatalog.Values.Where(routeCandidate =>
                            routeCandidate.PlanarConversion.ActualCopperLength +
                                1e-8d < sourceRouteCandidateLength));
                }

                if (!optimizationCancellationToken.IsCancellationRequested)
                {
                    var directOptimizationCandidateCacheKey =
                        LinkageRouteCandidateFactory.GetCandidateSignature(
                            sourceRouteCandidate) +
                        (includeDeepOptimizationCandidates
                            ? "||DIRECT"
                            : "||START-HORIZONTAL") +
                        "||SELECTED=" +
                        GetCandidateSetSignature(selectedOtherRouteCandidates);
                    var directOptimizationCandidates =
                        solutionOptimizationCandidateCache.GetOrAdd(
                            directOptimizationCandidateCacheKey,
                            ignoredCacheKey => new Lazy<IReadOnlyList<
                                LinkageRouteCandidate>>(
                                () => includeDeepOptimizationCandidates
                                    ? LinkageRouteCandidateFactory
                                        .BuildDirectGeometryOptimizationCandidates(
                                            RouteRequest,
                                            sourceRouteCandidate,
                                            FixedTerminalAndObstacleSolids,
                                            selectedOtherRouteCandidates,
                                            Math.Max(
                                                1,
                                                Math.Min(
                                                    16,
                                                    RouteRequest.SearchOptions
                                                        .MaximumClearanceRepairCandidatesPerState)),
                                            optimizationCancellationToken)
                                    : (IReadOnlyList<LinkageRouteCandidate>)LinkageRouteCandidateFactory
                                        .BuildStartHorizontalLapOptimizationCandidates(
                                            RouteRequest,
                                            sourceRouteCandidate,
                                            FixedTerminalAndObstacleSolids,
                                            selectedOtherRouteCandidates,
                                            Math.Max(
                                                1,
                                                Math.Min(
                                                    16,
                                                    RouteRequest.SearchOptions
                                                        .MaximumClearanceRepairCandidatesPerState)),
                                            optimizationCancellationToken)
                                        // 快速层同时尝试 4 段及以上排的减折弯段（总长不增）。
                                        .Concat(LinkageRouteCandidateFactory
                                            .BuildSegmentReductionOptimizationCandidates(
                                                RouteRequest,
                                                sourceRouteCandidate,
                                                FixedTerminalAndObstacleSolids,
                                                selectedOtherRouteCandidates,
                                                16,
                                                optimizationCancellationToken))
                                        .ToList(),
                                LazyThreadSafetyMode.ExecutionAndPublication))
                            .Value;
                    foreach (var directOptimizationCandidate in
                        directOptimizationCandidates)
                    {
                        RegisterSolutionOptimizationCandidate(
                            directOptimizationCandidate);
                        shorterCandidateNeighborhood.Add(
                            directOptimizationCandidate);
                    }
                }

                var primaryOptimizationCandidates =
                    OrderShorterOptimizationCandidates(
                        shorterCandidateNeighborhood,
                        sourceRouteCandidate,
                        64);
                var yieldedCandidateSignatures = new HashSet<string>(
                    StringComparer.Ordinal);
                foreach (var primaryOptimizationCandidate in
                    primaryOptimizationCandidates)
                {
                    yieldedCandidateSignatures.Add(LinkageRouteCandidateFactory
                        .GetCandidateSignature(primaryOptimizationCandidate));
                    yield return primaryOptimizationCandidate;
                }

                if (!includeDeepOptimizationCandidates ||
                    optimizationCancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                // 已搜索目录和当前几何局部邻域都无法完成跨组替换时，才惰性
                // 进入原有的绝缘临界、反向搭接和扩展路径枚举，避免阻塞首个
                // 后台优化结果，同时保留旧优化能力。
                var deepOptimizationCandidateCacheKey =
                    LinkageRouteCandidateFactory.GetCandidateSignature(
                        sourceRouteCandidate) +
                    "||DEEP||SELECTED=" +
                    GetCandidateSetSignature(selectedOtherRouteCandidates);
                var deepOptimizationCandidates = solutionOptimizationCandidateCache
                    .GetOrAdd(
                        deepOptimizationCandidateCacheKey,
                        ignoredCacheKey => new Lazy<IReadOnlyList<
                            LinkageRouteCandidate>>(
                            () => LinkageRouteCandidateFactory
                                .BuildSolutionLengthOptimizationCandidates(
                                    RouteRequest,
                                    sourceRouteCandidate,
                                    FixedTerminalAndObstacleSolids,
                                    selectedOtherRouteCandidates,
                                    optimizationCancellationToken),
                            LazyThreadSafetyMode.ExecutionAndPublication))
                    .Value;
                foreach (var deepOptimizationCandidate in
                    OrderShorterOptimizationCandidates(
                        deepOptimizationCandidates,
                        sourceRouteCandidate,
                        64))
                {
                    RegisterSolutionOptimizationCandidate(
                        deepOptimizationCandidate);
                    if (yieldedCandidateSignatures.Add(
                        LinkageRouteCandidateFactory.GetCandidateSignature(
                            deepOptimizationCandidate)))
                    {
                        yield return deepOptimizationCandidate;
                    }
                }
            }

            private static IReadOnlyList<LinkageRouteCandidate>
                OrderShorterOptimizationCandidates(
                    IEnumerable<LinkageRouteCandidate> routeCandidates,
                    LinkageRouteCandidate sourceRouteCandidate,
                    int maximumCandidateCount)
            {
                var sourceRouteCandidateLength = sourceRouteCandidate
                    ?.PlanarConversion?.ActualCopperLength ??
                    double.PositiveInfinity;
                return (routeCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(routeCandidate =>
                        routeCandidate?.PlanarConversion?.IsValid == true &&
                        (routeCandidate.PlanarConversion.ActualCopperLength +
                            1e-8d < sourceRouteCandidateLength ||
                         LinkageRouteCandidateFactory.IsImprovedOptimizationCandidate(
                             routeCandidate, sourceRouteCandidate)))
                    .GroupBy(LinkageRouteCandidateFactory.GetCandidateSignature)
                    .Select(candidateGroup => candidateGroup
                        .OrderBy(routeCandidate => routeCandidate
                            .PlanarConversion.ActualCopperLength)
                        .ThenBy(routeCandidate =>
                            routeCandidate.GeneratedBusbarPartCount)
                        .First())
                    .OrderBy(routeCandidate => LinkageRouteCandidateFactory
                        .GetSolutionOptimizationSectionPriority(
                            sourceRouteCandidate,
                            routeCandidate))
                    .ThenBy(routeCandidate => routeCandidate
                        .PlanarConversion.ActualCopperLength)
                    .ThenBy(routeCandidate => LinkageRouteTopologySignature
                        .CountCenterlineBends(routeCandidate.ManufacturableCenterline))
                    .ThenBy(routeCandidate =>
                        routeCandidate.GeneratedBusbarPartCount)
                    .ThenBy(
                        LinkageRouteCandidateFactory.GetCandidateSignature,
                        StringComparer.Ordinal)
                    .Take(Math.Max(1, maximumCandidateCount))
                    .ToList();
            }

            private void RecordTerminalOutputPostProcessingFailure(
                IEnumerable<LinkageRouteCandidate> completedOutputCandidates,
                LinkageRouteCandidate failedOutputCandidate)
            {
                if (failedOutputCandidate?.GroupRequest == null)
                {
                    return;
                }

                var requiredOutputClearance = failedOutputCandidate.GroupRequest
                    .GetRequiredInsulationClearance(OutputRouteRequest);
                var hasFiniteClearanceFailure =
                    failedOutputCandidate.FailureStage ==
                        LinkageRouteFailureStage.Clearance &&
                    !double.IsNaN(failedOutputCandidate.MinimumStaticClearance) &&
                    !double.IsInfinity(
                        failedOutputCandidate.MinimumStaticClearance);
                var recordedMinimumClearance = hasFiniteClearanceFailure
                    ? Math.Max(
                        0d,
                        failedOutputCandidate.MinimumStaticClearance)
                    : 0d;
                var recordedMaximumViolationDepth = hasFiniteClearanceFailure
                    ? Math.Max(
                        0d,
                        failedOutputCandidate.MaximumViolationDepth)
                    : 0d;
                RecordFailure(
                    completedOutputCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>(),
                    failedOutputCandidate,
                    new PairClearanceEvaluation
                    {
                        IsClear = false,
                        MinimumClearance = recordedMinimumClearance,
                        RequiredClearance = requiredOutputClearance,
                        ViolationCount = Math.Max(
                            1,
                            failedOutputCandidate.ViolationCount),
                        MaximumViolationDepth =
                            recordedMaximumViolationDepth,
                        CandidateSegmentIndex = -1,
                        SelectedSegmentIndex = -1,
                        ConflictingGroupKey = "<终端输出后处理>",
                        SuggestedSeparationDistance = Math.Max(
                            0d,
                            recordedMaximumViolationDepth),
                        SuggestedSeparationDirection =
                            failedOutputCandidate
                                .SuggestedClearanceSeparationDirection
                    },
                    "完整普通方案成立，但终端输出转换未完成：" +
                    (failedOutputCandidate.Diagnostic ?? "未提供诊断。"));
            }

            public void RecordFailure(
                IEnumerable<LinkageRouteCandidate> selectedCandidates,
                LinkageRouteCandidate failedCandidate,
                PairClearanceEvaluation pairEvaluation,
                string diagnosticMessage)
            {
                var selectedCandidateList = selectedCandidates.ToList();
                if (CouldImproveBestFailure(
                    selectedCandidateList.Count,
                    pairEvaluation.ViolationCount,
                    pairEvaluation.MaximumViolationDepth))
                {
                    var candidateFailureSolution = CreateCombinationFailure(
                        AlgorithmKind,
                        selectedCandidateList,
                        failedCandidate,
                        selectedCandidateList.Count,
                        pairEvaluation.ViolationCount,
                        pairEvaluation.MaximumViolationDepth,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} 生成顺序：{1}；最小距离={2:F6}，要求距离={3:F6}，" +
                            "最大侵入={4:F6}；冲突段={5}/{6}（组 {7}）；" +
                            "建议分离方向={8}，极限位移={9:F6}；" +
                            "组对违规分类：通用净距 {10}，搭接区域 {11}，同组铜排重叠 {12}。",
                            diagnosticMessage,
                            string.Join("→", selectedCandidateList
                                .Select(candidate =>
                                    candidate.GroupRequest.GroupKey)
                                .Concat(new[]
                                {
                                    failedCandidate.GroupRequest.GroupKey
                                })),
                            pairEvaluation.MinimumClearance,
                            pairEvaluation.RequiredClearance,
                            pairEvaluation.MaximumViolationDepth,
                            pairEvaluation.CandidateSegmentIndex,
                            pairEvaluation.SelectedSegmentIndex,
                            pairEvaluation.ConflictingGroupKey ?? "<障碍物>",
                            FormatDirection(
                                pairEvaluation.SuggestedSeparationDirection),
                            pairEvaluation.SuggestedSeparationDistance,
                            pairEvaluation.GeneralClearanceViolationCount,
                            pairEvaluation.LapClearanceViolationCount,
                            pairEvaluation.SameGroupOverlapCount));
                    lock (resultGate)
                    {
                        if (IsBetterFailure(
                            candidateFailureSolution,
                            bestFailureSolution))
                        {
                            bestFailureSolution = candidateFailureSolution;
                        }
                    }
                }

            }

            private bool CouldImproveBestFailure(
                int completedGroupCount,
                int violationCount,
                double maximumViolationDepth)
            {
                lock (resultGate)
                {
                    var currentDiagnostic = bestFailureSolution?.Diagnostics
                        .FirstOrDefault();
                    if (currentDiagnostic == null)
                    {
                        return true;
                    }

                    if (completedGroupCount !=
                        currentDiagnostic.CompletedGroupCount)
                    {
                        return completedGroupCount >
                            currentDiagnostic.CompletedGroupCount;
                    }

                    if (violationCount != currentDiagnostic.ViolationCount)
                    {
                        return violationCount < currentDiagnostic.ViolationCount;
                    }

                    return maximumViolationDepth <
                        currentDiagnostic.MaximumViolationDepth;
                }
            }

            private IReadOnlyList<LinkageRouteCandidate>
                TryFindBestPostProcessedCatalogCandidateSet()
            {
                var catalogValidationRequest = OutputRouteRequest
                    .CreateRequestedClearanceValidationRequest();
                var candidatePools = new List<List<LinkageRouteCandidate>>();
                foreach (var routeGroupRequest in RouteRequest.Groups)
                {
                    if (!solutionOptimizationCandidatesByGroup.TryGetValue(
                            routeGroupRequest.GroupKey ?? string.Empty,
                            out var registeredGroupCandidates))
                    {
                        return null;
                    }

                    var postProcessedPool = registeredGroupCandidates.Values
                        .Where(routeCandidate =>
                            routeCandidate?.PlanarConversion?.IsValid == true)
                        .GroupBy(
                            LinkageRouteCandidateFactory.GetCandidateSignature)
                        .Select(candidateGroup => candidateGroup.First())
                        .OrderBy(routeCandidate => routeCandidate
                            .PlanarConversion.ActualCopperLength)
                        .ThenBy(routeCandidate => routeCandidate.GeneratedBusbarPartCount)
                        .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                        .Take(Math.Max(
                            1,
                            RouteRequest.SearchOptions
                                .MaximumCandidatesPerGroup))
                        .Select(routeCandidate =>
                            CreateRequiredTerminalOutputCandidate(
                                catalogValidationRequest,
                                routeCandidate,
                                CancellationToken))
                        .Where(routeCandidate => routeCandidate?.IsValid == true)
                        .ToList();
                    if (postProcessedPool.Count == 0)
                    {
                        return null;
                    }

                    candidatePools.Add(postProcessedPool);
                }

                var orderedPoolIndices = Enumerable.Range(
                        0,
                        candidatePools.Count)
                    .OrderBy(poolIndex => candidatePools[poolIndex].Count)
                    .ToList();
                var selectedCandidates = new List<LinkageRouteCandidate>();
                List<LinkageRouteCandidate> bestCandidateSet = null;
                var bestMinimumClearance = 0d;
                var catalogSearchStopwatch = Stopwatch.StartNew();

                Action<int, double> searchCatalog = null;
                searchCatalog = (orderedPoolPosition, currentMinimumClearance) =>
                {
                    if (CancellationToken.IsCancellationRequested ||
                        catalogSearchStopwatch.ElapsedMilliseconds >=
                            RouteRequest.SearchOptions
                                .ClearanceOptimizationCatalogLimitMilliseconds ||
                        bestMinimumClearance + 1e-6d >=
                            OutputRouteRequest.InsulationClearance)
                    {
                        return;
                    }

                    if (orderedPoolPosition >= orderedPoolIndices.Count)
                    {
                        if (currentMinimumClearance >
                            bestMinimumClearance + 1e-6d)
                        {
                            bestMinimumClearance = currentMinimumClearance;
                            bestCandidateSet = selectedCandidates.ToList();
                        }
                        return;
                    }

                    var candidatePool = candidatePools[
                        orderedPoolIndices[orderedPoolPosition]];
                    var candidatesByCurrentClearance = candidatePool
                        .Select(routeCandidate =>
                        {
                            var routeCandidateMinimumClearance = Math.Min(
                                currentMinimumClearance,
                                GetExactStaticMinimumDistance(routeCandidate));
                            foreach (var selectedCandidate in
                                selectedCandidates)
                            {
                                routeCandidateMinimumClearance = Math.Min(
                                    routeCandidateMinimumClearance,
                                    GetExactPairMinimumDistance(
                                        routeCandidate,
                                        selectedCandidate));
                            }

                            return new KeyValuePair<LinkageRouteCandidate,
                                double>(
                                routeCandidate,
                                routeCandidateMinimumClearance);
                        })
                        .OrderByDescending(candidatePair => candidatePair.Value)
                        .ThenBy(candidatePair =>
                            candidatePair.Key.SearchPriority)
                        .ThenBy(candidatePair => candidatePair.Key
                            .PlanarConversion.ActualCopperLength)
                        .ToList();
                    foreach (var candidatePair in candidatesByCurrentClearance)
                    {
                        var routeCandidate = candidatePair.Key;
                        var candidateMinimumClearance = candidatePair.Value;

                        if (candidateMinimumClearance <=
                            Math.Max(1e-6d, bestMinimumClearance + 1e-6d))
                        {
                            continue;
                        }

                        selectedCandidates.Add(routeCandidate);
                        searchCatalog(
                            orderedPoolPosition + 1,
                            candidateMinimumClearance);
                        selectedCandidates.RemoveAt(
                            selectedCandidates.Count - 1);
                    }
                };
                searchCatalog(0, double.PositiveInfinity);
                return bestCandidateSet;
            }

            private bool IsPublishablePostProcessedSolution(
                LinkageRouteSolution routeSolution)
            {
                return routeSolution != null &&
                    !double.IsNaN(routeSolution.MinimumClearance) &&
                    !double.IsNegativeInfinity(routeSolution.MinimumClearance) &&
                    routeSolution.MinimumClearance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison >=
                        OutputRouteRequest.InsulationClearance &&
                    TryValidatePhysicalSolutionMaterialization(
                        OutputRouteRequest,
                        routeSolution);
            }

            private IReadOnlyList<LinkageRouteCandidate>
                TryFindBestOrdinaryCatalogCandidateSet()
            {
                var candidatePools = new List<List<LinkageRouteCandidate>>();
                foreach (var routeGroupRequest in RouteRequest.Groups)
                {
                    if (!solutionOptimizationCandidatesByGroup.TryGetValue(
                            routeGroupRequest.GroupKey ?? string.Empty,
                            out var registeredGroupCandidates))
                    {
                        return null;
                    }

                    var candidatePool = registeredGroupCandidates.Values
                        .Where(routeCandidate =>
                            routeCandidate?.PlanarConversion?.IsValid == true)
                        .GroupBy(
                            LinkageRouteCandidateFactory.GetCandidateSignature)
                        .Select(candidateGroup => candidateGroup.First())
                        .OrderBy(routeCandidate => routeCandidate
                            .PlanarConversion.ActualCopperLength)
                        .ThenBy(routeCandidate => routeCandidate.GeneratedBusbarPartCount)
                        .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                        .Take(Math.Max(
                            1,
                            RouteRequest.SearchOptions
                                .MaximumCandidatesPerGroup))
                        .ToList();
                    if (candidatePool.Count == 0)
                    {
                        return null;
                    }

                    candidatePools.Add(candidatePool);
                }

                var orderedPoolIndices = Enumerable.Range(
                        0,
                        candidatePools.Count)
                    .OrderBy(poolIndex => candidatePools[poolIndex].Count)
                    .ToList();
                var selectedCandidates = new List<LinkageRouteCandidate>();
                List<LinkageRouteCandidate> bestCandidateSet = null;
                var bestMinimumClearance = 0d;
                var catalogSearchStopwatch = Stopwatch.StartNew();
                Action<int, double> searchCatalog = null;
                searchCatalog = (orderedPoolPosition, currentMinimumClearance) =>
                {
                    if (CancellationToken.IsCancellationRequested ||
                        catalogSearchStopwatch.ElapsedMilliseconds >= 10000L)
                    {
                        return;
                    }

                    if (orderedPoolPosition >= orderedPoolIndices.Count)
                    {
                        if (currentMinimumClearance + 1e-6d <
                                OutputRouteRequest.InsulationClearance &&
                            currentMinimumClearance >
                            bestMinimumClearance + 1e-6d)
                        {
                            bestMinimumClearance = currentMinimumClearance;
                            bestCandidateSet = selectedCandidates.ToList();
                        }
                        return;
                    }

                    var candidatesByCurrentClearance = candidatePools[
                            orderedPoolIndices[orderedPoolPosition]]
                        .Select(routeCandidate =>
                        {
                            var routeCandidateMinimumClearance = Math.Min(
                                currentMinimumClearance,
                                GetExactStaticMinimumDistance(routeCandidate));
                            foreach (var selectedCandidate in
                                selectedCandidates)
                            {
                                routeCandidateMinimumClearance = Math.Min(
                                    routeCandidateMinimumClearance,
                                    GetExactPairMinimumDistance(
                                        routeCandidate,
                                        selectedCandidate));
                            }

                            return new KeyValuePair<LinkageRouteCandidate,
                                double>(
                                routeCandidate,
                                routeCandidateMinimumClearance);
                        })
                        .OrderByDescending(candidatePair => candidatePair.Value)
                        .ThenBy(candidatePair =>
                            candidatePair.Key.SearchPriority)
                        .ThenBy(candidatePair => candidatePair.Key
                            .PlanarConversion.ActualCopperLength)
                        .ToList();
                    foreach (var candidatePair in candidatesByCurrentClearance)
                    {
                        if (candidatePair.Value <=
                            bestMinimumClearance + 1e-6d)
                        {
                            continue;
                        }

                        selectedCandidates.Add(candidatePair.Key);
                        searchCatalog(
                            orderedPoolPosition + 1,
                            candidatePair.Value);
                        selectedCandidates.RemoveAt(
                            selectedCandidates.Count - 1);
                    }
                };
                searchCatalog(0, double.PositiveInfinity);
                return bestCandidateSet;
            }

            private string DescribeCandidateSetMinimumClearance(
                IReadOnlyList<LinkageRouteCandidate> candidateSet)
            {
                var minimumDescription = "<无>";
                var minimumDistance = double.PositiveInfinity;
                foreach (var routeCandidate in candidateSet)
                {
                    var staticDistance = GetExactStaticMinimumDistance(
                        routeCandidate);
                    if (staticDistance < minimumDistance)
                    {
                        minimumDistance = staticDistance;
                        minimumDescription = routeCandidate.GroupRequest.GroupKey +
                            "↔固定/障碍=" + staticDistance.ToString(
                                "F6",
                                CultureInfo.InvariantCulture);
                    }
                }

                for (var firstCandidateIndex = 0;
                    firstCandidateIndex < candidateSet.Count;
                    firstCandidateIndex++)
                {
                    for (var secondCandidateIndex = firstCandidateIndex + 1;
                        secondCandidateIndex < candidateSet.Count;
                        secondCandidateIndex++)
                    {
                        var pairDistance = GetExactPairMinimumDistance(
                            candidateSet[firstCandidateIndex],
                            candidateSet[secondCandidateIndex]);
                        if (pairDistance < minimumDistance)
                        {
                            minimumDistance = pairDistance;
                            minimumDescription = candidateSet[firstCandidateIndex]
                                .GroupRequest.GroupKey + "↔" +
                                candidateSet[secondCandidateIndex]
                                    .GroupRequest.GroupKey + "=" +
                                pairDistance.ToString(
                                    "F6",
                                    CultureInfo.InvariantCulture);
                        }
                    }
                }

                return minimumDescription;
            }

            private LinkageRouteCandidate
                CreateTerminalOutputCandidateWithFallback(
                LinkageMultiGroupRouteRequest requestedClearanceValidationRequest,
                LinkageRouteCandidate ordinaryCandidate,
                IEnumerable<LinkageRouteCandidate> selectedOutputCandidates)
            {
                var candidateAlternatives = new List<LinkageRouteCandidate>
                {
                    ordinaryCandidate
                };
                if (solutionOptimizationCandidatesByGroup.TryGetValue(
                        ordinaryCandidate.GroupRequest.GroupKey ?? string.Empty,
                        out var registeredGroupCandidates))
                {
                    candidateAlternatives.AddRange(
                        registeredGroupCandidates.Values);
                }

                LinkageRouteCandidate firstFailedPostProcessedCandidate = null;
                LinkageRouteCandidate bestPostProcessedCandidate = null;
                var bestPostProcessedMinimumClearance =
                    double.NegativeInfinity;
                var selectedOutputCandidateList = (selectedOutputCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(routeCandidate => routeCandidate?.IsValid == true)
                    .ToList();
                foreach (var candidateAlternative in candidateAlternatives
                    .Where(routeCandidate =>
                        routeCandidate?.PlanarConversion?.IsValid == true)
                    .GroupBy(LinkageRouteCandidateFactory.GetCandidateSignature)
                    .Select(candidateGroup => candidateGroup.First())
                    .OrderBy(routeCandidate =>
                        ReferenceEquals(routeCandidate, ordinaryCandidate)
                            ? 0
                            : 1)
                    .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                    .ThenBy(routeCandidate =>
                        routeCandidate.PlanarConversion.ActualCopperLength))
                {
                    if (CancellationToken.IsCancellationRequested)
                    {
                        return firstFailedPostProcessedCandidate;
                    }

                    var postProcessedCandidate =
                        CreateRequiredTerminalOutputCandidate(
                            requestedClearanceValidationRequest,
                            candidateAlternative,
                            CancellationToken);
                    if (postProcessedCandidate?.IsValid == true)
                    {
                        var candidateMinimumClearance =
                            GetExactStaticMinimumDistance(
                                postProcessedCandidate);
                        foreach (var selectedOutputCandidate in
                            selectedOutputCandidateList)
                        {
                            candidateMinimumClearance = Math.Min(
                                candidateMinimumClearance,
                                GetExactPairMinimumDistance(
                                    postProcessedCandidate,
                                    selectedOutputCandidate));
                        }

                        if (candidateMinimumClearance >
                            bestPostProcessedMinimumClearance + 1e-6d)
                        {
                            bestPostProcessedMinimumClearance =
                                candidateMinimumClearance;
                            bestPostProcessedCandidate =
                                postProcessedCandidate;
                            if (candidateMinimumClearance + 1e-6d >=
                                OutputRouteRequest.InsulationClearance)
                            {
                                break;
                            }
                        }
                    }

                    if (firstFailedPostProcessedCandidate == null)
                    {
                        firstFailedPostProcessedCandidate =
                            postProcessedCandidate;
                    }
                }

                return bestPostProcessedCandidate ??
                    firstFailedPostProcessedCandidate;
            }

            public void RecordFinalExactClearanceFailure(
                IEnumerable<LinkageRouteCandidate> completeCandidateSet,
                LinkageRouteCandidate failedCandidate,
                double actualMinimumClearance,
                double requiredMinimumClearance,
                string conflictingGroupKey)
            {
                var completedOtherCandidates = (completeCandidateSet ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(candidate => !ReferenceEquals(candidate, failedCandidate))
                    .ToList();
                RecordFailure(
                    completedOtherCandidates,
                    failedCandidate,
                    new PairClearanceEvaluation
                    {
                        IsClear = false,
                        MinimumClearance = actualMinimumClearance,
                        RequiredClearance = requiredMinimumClearance,
                        ViolationCount = 1,
                        MaximumViolationDepth = Math.Max(
                            0d,
                            requiredMinimumClearance - actualMinimumClearance),
                        CandidateSegmentIndex = -1,
                        SelectedSegmentIndex = -1,
                        ConflictingGroupKey = conflictingGroupKey,
                        SuggestedSeparationDistance = Math.Max(
                            0d,
                            requiredMinimumClearance - actualMinimumClearance)
                    },
                    "完整方案发布前的实际逐排长方体绝缘复核失败。" );
            }

            public double GetExactPairMinimumDistance(
                LinkageRouteCandidate firstCandidate,
                LinkageRouteCandidate secondCandidate)
            {
                var requiredCandidatePairInsulationClearance =
                    GetRequiredCandidatePairInsulationClearance(
                        firstCandidate,
                        secondCandidate);
                var exactPairCacheKey = GetPairCacheKey(
                    firstCandidate,
                    secondCandidate,
                    requiredCandidatePairInsulationClearance);
                if (exactPairDistanceCache.TryGetValue(
                    exactPairCacheKey,
                    out var cachedExactPairMinimumDistance))
                {
                    return cachedExactPairMinimumDistance;
                }

                var exactPairMinimumDistance = GetExactMinimumDistance(
                    firstCandidate.ActualClearanceSolids,
                    secondCandidate.ActualClearanceSolids,
                    true,
                    CancellationToken);
                if (!CancellationToken.IsCancellationRequested)
                {
                    exactPairDistanceCache.TryAdd(
                        exactPairCacheKey,
                        exactPairMinimumDistance);
                }

                return exactPairMinimumDistance;
            }

            public double GetExactStaticMinimumDistance(
                LinkageRouteCandidate routeCandidate)
            {
                var candidateSignature = CandidateCacheKey.Create(routeCandidate);
                if (exactStaticDistanceCache.TryGetValue(
                    candidateSignature,
                    out var cachedExactStaticMinimumDistance))
                {
                    return cachedExactStaticMinimumDistance;
                }

                var exactStaticMinimumDistance = GetExactMinimumDistance(
                    routeCandidate.ActualClearanceSolids,
                    FixedTerminalAndObstacleSolids.Where(staticSolid =>
                        ParticipatesInInsulationClearance(staticSolid)),
                    true,
                    CancellationToken);
                if (!CancellationToken.IsCancellationRequested)
                {
                    exactStaticDistanceCache.TryAdd(
                        candidateSignature,
                        exactStaticMinimumDistance);
                }

                return exactStaticMinimumDistance;
            }

            public List<LinkageRouteSolution> GetRankedSolutions()
            {
                lock (resultGate)
                {
                    var rankedSolutions = successfulSolutions
                        .OrderBy(solutionPair => solutionPair.Value.ActualCopperLength)
                        .ThenBy(solutionPair =>
                            solutionPair.Value.GeneratedBusbarPartCount)
                        .ThenBy(solutionPair => LinkageRouteTopologySignature
                            .CreateStableGeometrySignature(solutionPair.Value),
                            StringComparer.Ordinal)
                        .ThenBy(solutionPair => solutionPair.Key, StringComparer.Ordinal)
                        .Select(solutionPair => CloneSolutionSnapshot(
                            solutionPair.Value))
                        .ToList();
                    for (var solutionIndex = 0;
                        solutionIndex < rankedSolutions.Count;
                        solutionIndex++)
                    {
                        rankedSolutions[solutionIndex].SchemeIndex = solutionIndex + 1;
                    }

                    return rankedSolutions;
                }
            }

            private static LinkageRouteSolution CloneSolutionSnapshot(
                LinkageRouteSolution routeSolution)
            {
                var clonedSolution = LinkageRouteWorkerContractMapper.FromWorkerSolution(
                    LinkageRouteWorkerContractMapper.ToWorkerSolution(
                        routeSolution));
                if (routeSolution?.GroupSolutions == null ||
                    clonedSolution?.GroupSolutions == null)
                {
                    return clonedSolution;
                }

                foreach (var sourceGroupSolution in routeSolution.GroupSolutions)
                {
                    var clonedGroupSolution = clonedSolution.GroupSolutions
                        .FirstOrDefault(groupSolution => string.Equals(
                            groupSolution?.GroupKey,
                            sourceGroupSolution?.GroupKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (sourceGroupSolution == null || clonedGroupSolution == null)
                    {
                        continue;
                    }

                    // Internal reconstruction choice is intentionally not a Worker DTO member.
                    clonedGroupSolution.USplitContactSide = sourceGroupSolution.USplitContactSide;

                    foreach (var physicalSectionIndex in sourceGroupSolution
                        .DoubleStartPhysicalBridgeSectionIndices)
                    {
                        clonedGroupSolution.DoubleStartPhysicalBridgeSectionIndices
                            .Add(physicalSectionIndex);
                    }
                    foreach (var bridgeMemberIndex in sourceGroupSolution
                        .ExpectedDoubleStartBridgeMemberIndices)
                    {
                        clonedGroupSolution.ExpectedDoubleStartBridgeMemberIndices
                            .Add(bridgeMemberIndex);
                    }

                    CopyPhysicalSectionIndices(
                        sourceGroupSolution.PlanarParts,
                        clonedGroupSolution.PlanarParts);
                    for (var accessoryIndex = 0; accessoryIndex < sourceGroupSolution.Accessories.Count &&
                        accessoryIndex < clonedGroupSolution.Accessories.Count; accessoryIndex++)
                    {
                        clonedGroupSolution.Accessories[accessoryIndex].SupportBlockKey =
                            sourceGroupSolution.Accessories[accessoryIndex].SupportBlockKey;
                    }
                    foreach (var sourceStackMember in
                        sourceGroupSolution.StackMembers ??
                            new List<LinkageStackedRouteMember>())
                    {
                        var clonedStackMember = clonedGroupSolution.StackMembers
                            ?.FirstOrDefault(stackMember =>
                                stackMember?.MemberIndex ==
                                    sourceStackMember.MemberIndex);
                        CopyPhysicalSectionIndices(
                            sourceStackMember.PlanarParts,
                            clonedStackMember?.PlanarParts);
                    }
                }

                return clonedSolution;
            }

            private static void CopyPhysicalSectionIndices(
                IList<PlanarBendBusbarSetting> sourceSettings,
                IList<PlanarBendBusbarSetting> clonedSettings)
            {
                if (sourceSettings == null || clonedSettings == null)
                {
                    return;
                }

                var sharedSettingCount = Math.Min(
                    sourceSettings.Count,
                    clonedSettings.Count);
                for (var settingIndex = 0;
                    settingIndex < sharedSettingCount;
                    settingIndex++)
                {
                    if (sourceSettings[settingIndex] != null &&
                        clonedSettings[settingIndex] != null)
                    {
                        clonedSettings[settingIndex].PhysicalSectionIndex =
                            sourceSettings[settingIndex].PhysicalSectionIndex;
                        clonedSettings[settingIndex].SpecificationSectionIndex =
                            sourceSettings[settingIndex].SpecificationSectionIndex;
                    }
                }
            }

            public LinkageRouteSolution GetBestFailureSolution()
            {
                lock (resultGate)
                {
                    if (string.IsNullOrWhiteSpace(firstFinalOutputRejectionReason))
                        return bestFailureSolution;
                    // 仅在结束时附加诊断，不参与候选排序，不发布拒绝方案的几何。
                    // 以前最终实体门禁直接丢弃原因，可能导致无解时连失败记录也没有。
                    var reportedFailure = bestFailureSolution ?? new LinkageRouteSolution
                    {
                        AlgorithmKind = AlgorithmKind,
                        IsSuccess = false
                    };
                    if (!reportedFailure.Diagnostics.Any(diagnostic =>
                        diagnostic.Message == firstFinalOutputRejectionReason))
                    {
                        reportedFailure.Diagnostics.Add(new LinkageRouteFailureDiagnostic
                        {
                            Stage = firstFinalOutputRejectionReason.StartsWith("最终搭接面绝缘违规",
                                StringComparison.Ordinal) ? LinkageRouteFailureStage.Clearance : LinkageRouteFailureStage.Conversion,
                            Message = firstFinalOutputRejectionReason
                        });
                    }
                    return reportedFailure;
                }
            }

            private static PairClearanceEvaluation EvaluateCandidatePair(
                LinkageRouteCandidate firstCandidate,
                LinkageRouteCandidate secondCandidate,
                double requiredClearance,
                CancellationToken cancellationToken)
            {
                return EvaluateCandidateSolidCollections(
                    firstCandidate.ActualClearanceSolids,
                    secondCandidate.ActualClearanceSolids,
                    secondCandidate.GroupRequest.GroupKey,
                    requiredClearance,
                    cancellationToken);
            }

            private static PairClearanceEvaluation
                EvaluateCandidateSolidCollections(
                    IEnumerable<LinkageRouteSolid> firstGeneratedSolids,
                    IEnumerable<LinkageRouteSolid> secondGeneratedSolids,
                    string conflictingGroupKey,
                    double requiredClearance,
                    CancellationToken cancellationToken)
            {
                var pairEvaluation = new PairClearanceEvaluation
                {
                    IsClear = true,
                    RequiredClearance = requiredClearance
                };
                foreach (var firstSolid in firstGeneratedSolids)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // 取消结果仅用于让当前递归立即退出；调用点必须随后检查
                        // ShouldStop，不能把 +∞/0 组合解释为真实净距结论。
                        pairEvaluation.IsClear = false;
                        return pairEvaluation;
                    }

                    foreach (var secondSolid in secondGeneratedSolids)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            // 同上：这是取消哨兵，不是可缓存的几何评估。
                            pairEvaluation.IsClear = false;
                            return pairEvaluation;
                        }

                        var isSameClearanceGroup =
                            !string.IsNullOrWhiteSpace(
                                firstSolid.ClearanceGroupKey) &&
                            string.Equals(
                                firstSolid.ClearanceGroupKey,
                                secondSolid.ClearanceGroupKey,
                                StringComparison.OrdinalIgnoreCase);
                        if (isSameClearanceGroup)
                        {
                            if (firstSolid.IsLapClearanceEnvelope ||
                                secondSolid.IsLapClearanceEnvelope)
                            {
                                continue;
                            }

                            if (!LinkageRouteSolidDistance
                                .HasPositiveVolumeIntersection(
                                    firstSolid,
                                    secondSolid,
                                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                            {
                                continue;
                            }

                            pairEvaluation.IsClear = false;
                            pairEvaluation.MinimumClearance = 0d;
                            pairEvaluation.ViolationCount++;
                            pairEvaluation.SameGroupOverlapCount++;
                            var samePhaseSeparationDirection =
                                GetSuggestedAxisSeparationDirection(
                                    firstSolid,
                                    secondSolid,
                                    0d);
                            var samePhaseSeparationDistance =
                                Math.Max(
                                    1e-6d,
                                    GetRequiredDirectionalSeparationDistance(
                                        firstSolid,
                                        secondSolid,
                                        samePhaseSeparationDirection,
                                        0d));
                            if (samePhaseSeparationDistance >
                                pairEvaluation.MaximumViolationDepth)
                            {
                                pairEvaluation.MaximumViolationDepth =
                                    samePhaseSeparationDistance;
                                pairEvaluation.CandidateSegmentIndex =
                                    firstSolid.SegmentIndex;
                                pairEvaluation.SelectedSegmentIndex =
                                    secondSolid.SegmentIndex;
                                pairEvaluation.ConflictingGroupKey =
                                    conflictingGroupKey;
                                pairEvaluation.SuggestedSeparationDirection =
                                    samePhaseSeparationDirection;
                                pairEvaluation.SuggestedSeparationDistance =
                                    samePhaseSeparationDistance;
                            }

                            continue;
                        }

                        var clearanceEvaluation =
                            LinkageRouteClearanceEvaluator.EvaluatePairWithBroadPhase(
                                firstSolid,
                                secondSolid,
                                requiredClearance,
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                        if (!clearanceEvaluation.IsLapEnvelopePair)
                        {
                            pairEvaluation.MinimumClearance = Math.Min(
                                pairEvaluation.MinimumClearance,
                                clearanceEvaluation.ActualDistance);
                        }
                        if (!clearanceEvaluation.IsClear)
                        {
                            pairEvaluation.IsClear = false;
                            pairEvaluation.ViolationCount++;
                            if (clearanceEvaluation.IsLapEnvelopePair)
                                pairEvaluation.LapClearanceViolationCount++;
                            else
                                pairEvaluation.GeneralClearanceViolationCount++;
                            // 单侧盒已包含搭接绝缘距离。其净距要求为零，ViolationDepth
                            // 不能表示相交程度；应记录退出实体所需位移，且不能再加通用距离。
                            if (!clearanceEvaluation.IsLapEnvelopePair &&
                                clearanceEvaluation.ViolationDepth <= pairEvaluation.MaximumViolationDepth)
                                continue;
                            Vector3d pairSeparationDirection;
                            double pairSeparationDistance;
                            if (clearanceEvaluation.IsLapEnvelopePair)
                            {
                                pairSeparationDistance = LinkageRouteSolidDistance.GetOverlapAxisExitDistance(
                                    firstSolid, secondSolid, out pairSeparationDirection);
                            }
                            else
                            {
                                pairSeparationDirection = GetSuggestedAxisSeparationDirection(
                                    firstSolid, secondSolid, requiredClearance);
                                pairSeparationDistance = GetRequiredDirectionalSeparationDistance(
                                    firstSolid, secondSolid, pairSeparationDirection, requiredClearance);
                            }
                            var effectivePairViolationDepth = clearanceEvaluation.IsLapEnvelopePair
                                ? pairSeparationDistance : clearanceEvaluation.ViolationDepth;
                            if (effectivePairViolationDepth >
                                pairEvaluation.MaximumViolationDepth)
                            {
                                pairEvaluation.MaximumViolationDepth =
                                    effectivePairViolationDepth;
                                pairEvaluation.CandidateSegmentIndex =
                                    firstSolid.SegmentIndex;
                                pairEvaluation.SelectedSegmentIndex =
                                    secondSolid.SegmentIndex;
                                pairEvaluation.ConflictingGroupKey =
                                    conflictingGroupKey;
                                pairEvaluation.SuggestedSeparationDirection =
                                    pairSeparationDirection;
                                pairEvaluation.SuggestedSeparationDistance =
                                    pairSeparationDistance;
                            }
                        }
                    }
                }

                return pairEvaluation;
            }

            private double GetRequiredCandidatePairInsulationClearance(
                LinkageRouteCandidate firstCandidate,
                LinkageRouteCandidate secondCandidate)
            {
                var firstCandidateRequiredInsulationClearance = firstCandidate
                    .GroupRequest
                    .GetRequiredInsulationClearance(RouteRequest);
                var secondCandidateRequiredInsulationClearance = secondCandidate
                    .GroupRequest
                    .GetRequiredInsulationClearance(RouteRequest);
                return Math.Max(
                    firstCandidateRequiredInsulationClearance,
                    secondCandidateRequiredInsulationClearance);
            }

            private static Vector3d GetSuggestedAxisSeparationDirection(
                LinkageRouteSolid movableSolid,
                LinkageRouteSolid fixedSolid,
                double requiredClearance)
            {
                var fixedToMovableCenter = new Vector3d(
                    fixedSolid.Center,
                    movableSolid.Center);
                var axisSeparationDirections = new[]
                {
                    new Vector3d(
                        fixedToMovableCenter.X < 0d ? -1d : 1d,
                        0d,
                        0d),
                    new Vector3d(
                        0d,
                        fixedToMovableCenter.Y < 0d ? -1d : 1d,
                        0d),
                    new Vector3d(
                        0d,
                        0d,
                        fixedToMovableCenter.Z < 0d ? -1d : 1d)
                };
                return axisSeparationDirections
                    .Select(separationDirection => new
                    {
                        Direction = separationDirection,
                        RequiredMovement =
                            GetRequiredDirectionalSeparationDistance(
                                movableSolid,
                                fixedSolid,
                                separationDirection,
                                requiredClearance)
                    })
                    .OrderBy(directionWithMovement =>
                        directionWithMovement.RequiredMovement)
                    .ThenByDescending(directionWithMovement => Math.Abs(
                        GetVectorDotProduct(
                            fixedToMovableCenter,
                            directionWithMovement.Direction)))
                    .Select(directionWithMovement => directionWithMovement.Direction)
                    .First();
            }

            private static double GetRequiredDirectionalSeparationDistance(
                LinkageRouteSolid movableSolid,
                LinkageRouteSolid fixedSolid,
                Vector3d separationDirection,
                double requiredClearance)
            {
                if (movableSolid == null || fixedSolid == null ||
                    separationDirection == null ||
                    separationDirection.Norm <= 1e-8d)
                {
                    return 0d;
                }

                var normalizedSeparationDirection = separationDirection.Normalized;
                var movableCenterProjection = ProjectPointOntoDirection(
                    movableSolid.Center,
                    normalizedSeparationDirection);
                var fixedCenterProjection = ProjectPointOntoDirection(
                    fixedSolid.Center,
                    normalizedSeparationDirection);
                var movableProjectionRadius = GetSolidProjectionRadius(
                    movableSolid,
                    normalizedSeparationDirection);
                var fixedProjectionRadius = GetSolidProjectionRadius(
                    fixedSolid,
                    normalizedSeparationDirection);
                return Math.Max(
                    0d,
                    fixedCenterProjection + fixedProjectionRadius +
                    Math.Max(0d, requiredClearance) -
                    (movableCenterProjection - movableProjectionRadius));
            }

            private static double ProjectPointOntoDirection(
                Point3d sourcePoint,
                Vector3d normalizedDirection)
            {
                return sourcePoint.X * normalizedDirection.X +
                    sourcePoint.Y * normalizedDirection.Y +
                    sourcePoint.Z * normalizedDirection.Z;
            }

            private static double GetSolidProjectionRadius(
                LinkageRouteSolid routeSolid,
                Vector3d normalizedDirection)
            {
                return Math.Abs(GetVectorDotProduct(
                           routeSolid.LengthDirection,
                           normalizedDirection)) * routeSolid.HalfLength +
                    Math.Abs(GetVectorDotProduct(
                           routeSolid.WidthDirection,
                           normalizedDirection)) * routeSolid.HalfWidth +
                    Math.Abs(GetVectorDotProduct(
                           routeSolid.ThicknessDirection,
                           normalizedDirection)) * routeSolid.HalfThickness;
            }

            private static double GetVectorDotProduct(
                Vector3d firstDirection,
                Vector3d secondDirection)
            {
                return firstDirection.X * secondDirection.X +
                    firstDirection.Y * secondDirection.Y +
                    firstDirection.Z * secondDirection.Z;
            }

            private static string FormatDirection(Vector3d separationDirection)
            {
                if (separationDirection == null)
                {
                    return "<未知>";
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "({0:F0},{1:F0},{2:F0})",
                    separationDirection.X,
                    separationDirection.Y,
                    separationDirection.Z);
            }

            private static double GetExactMinimumDistance(
                IEnumerable<LinkageRouteSolid> firstSolids,
                IEnumerable<LinkageRouteSolid> secondSolids,
                bool excludeSameGroup,
                CancellationToken cancellationToken)
            {
                var minimumDistance = double.PositiveInfinity;
                foreach (var firstSolid in firstSolids)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return minimumDistance;
                    }

                    foreach (var secondSolid in secondSolids)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return minimumDistance;
                        }

                        if (excludeSameGroup &&
                            !string.IsNullOrWhiteSpace(
                                firstSolid.ClearanceGroupKey) &&
                            string.Equals(
                                firstSolid.ClearanceGroupKey,
                                secondSolid.ClearanceGroupKey,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (firstSolid.IsLapClearanceEnvelope ||
                            secondSolid.IsLapClearanceEnvelope)
                        {
                            continue;
                        }

                        minimumDistance = Math.Min(
                            minimumDistance,
                            LinkageRouteSolidDistance.GetMinimumDistance(
                                firstSolid,
                                secondSolid));
                    }
                }

                return minimumDistance;
            }
        }

        private sealed class CombinationSearchStage : IDisposable
        {
            private readonly bool stopAfterFirstSuccessfulSolution;
            private readonly CancellationTokenSource stageCancellationSource;
            private int successfulStopRequested;

            public CombinationSearchStage(
                bool stopAfterFirstSuccessfulSolution,
                CancellationToken cancellationToken,
                int maximumStageMilliseconds,
                bool allowClearanceExpansionCandidates,
                bool allowPairViolationDirectedCandidates,
                bool allowDeepClearanceExpansionCandidates,
                bool exploreAllPoolOrders)
            {
                this.stopAfterFirstSuccessfulSolution =
                    stopAfterFirstSuccessfulSolution;
                AllowClearanceExpansionCandidates =
                    allowClearanceExpansionCandidates;
                AllowPairViolationDirectedCandidates =
                    allowPairViolationDirectedCandidates;
                AllowDeepClearanceExpansionCandidates =
                    allowDeepClearanceExpansionCandidates;
                ExploreAllPoolOrders = exploreAllPoolOrders;
                stageCancellationSource = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                if (maximumStageMilliseconds != Timeout.Infinite)
                {
                    stageCancellationSource.CancelAfter(
                        Math.Max(1, maximumStageMilliseconds));
                }
            }

            public CancellationToken CancellationToken =>
                stageCancellationSource.Token;

            public bool AllowClearanceExpansionCandidates { get; }

            public bool AllowPairViolationDirectedCandidates { get; }

            public bool AllowDeepClearanceExpansionCandidates { get; }

            public bool ExploreAllPoolOrders { get; }

            public bool ShouldStop => stageCancellationSource.IsCancellationRequested ||
                Volatile.Read(ref successfulStopRequested) != 0;

            public void NotifySuccessfulSolution()
            {
                if (stopAfterFirstSuccessfulSolution)
                {
                    Interlocked.Exchange(ref successfulStopRequested, 1);
                    stageCancellationSource.Cancel();
                }
            }

            public void Dispose()
            {
                stageCancellationSource.Dispose();
            }
        }

        private sealed class RootSearchBranch
        {
            public LinkageRouteCandidatePool CandidatePool { get; set; }
            public LinkageRouteCandidate RouteCandidate { get; set; }
        }

        private sealed class PairClearanceEvaluation
        {
            public bool IsClear { get; set; }
            public int ViolationCount { get; set; }
            public int GeneralClearanceViolationCount { get; set; }
            public int LapClearanceViolationCount { get; set; }
            public int SameGroupOverlapCount { get; set; }
            public double MaximumViolationDepth { get; set; }
            public double MinimumClearance { get; set; } = double.PositiveInfinity;
            public double RequiredClearance { get; set; }
            public int CandidateSegmentIndex { get; set; } = -1;
            public int SelectedSegmentIndex { get; set; } = -1;
            public string ConflictingGroupKey { get; set; }
            public double SuggestedSeparationDistance { get; set; }
            public Vector3d SuggestedSeparationDirection { get; set; }
        }

        private sealed class InlineProgress<TProgress> : IProgress<TProgress>
        {
            private readonly Action<TProgress> reportAction;

            public InlineProgress(Action<TProgress> reportAction)
            {
                this.reportAction = reportAction ??
                    throw new ArgumentNullException(nameof(reportAction));
            }

            public void Report(TProgress value)
            {
                reportAction(value);
            }
        }
    }
}
