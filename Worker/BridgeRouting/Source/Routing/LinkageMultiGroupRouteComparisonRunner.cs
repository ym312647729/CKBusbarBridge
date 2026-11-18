using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.Routing.Algorithms;
using RouteLinkBar.Algorithm.Transport;

namespace RouteLinkBar.Algorithm.Routing
{
    public static class LinkageMultiGroupRouteComparisonRunner
    {
        public static async Task<LinkageRouteComparisonResult> RunAsync(
            LinkageMultiGroupRouteRequest routeRequest,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            using (var runtimeControl = new LinkageRouteSearchRuntimeControl())
            {
                return await RunAsync(
                        routeRequest,
                        progress,
                        runtimeControl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public static async Task<LinkageRouteComparisonResult> RunAsync(
            LinkageMultiGroupRouteRequest routeRequest,
            IProgress<LinkageRouteProgress> progress,
            LinkageRouteSearchRuntimeControl runtimeControl,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null)
            {
                throw new ArgumentNullException(nameof(routeRequest));
            }

            if (runtimeControl == null)
            {
                throw new ArgumentNullException(nameof(runtimeControl));
            }

            routeRequest.Validate();
            routeRequest.RuntimeControl = runtimeControl;
            var comparisonStopwatch = Stopwatch.StartNew();
            var comparisonResult = new LinkageRouteComparisonResult();
            var firstSolutionMilliseconds = 0L;
            var firstSolutionGate = new object();
            var primarySearchProgress = new InlineProgress<LinkageRouteProgress>(
                progressUpdate =>
                {
                    StampPrimarySearchSolution(
                        progressUpdate?.PublishedSolution,
                        routeRequest.InsulationClearance,
                        routeRequest.LapInsulationClearance);
                    if (progressUpdate?.PublishedSolution?
                            .IsRelaxedClearanceSolution == true)
                    {
                        progressUpdate.PublishedSolution = null;
                        progressUpdate.Message =
                            "候选实际绝缘距离小于请求值，已淘汰；算法不会降低绝缘距离继续搜索。";
                    }

                    if (progressUpdate?.PublishedSolution?.IsSuccess == true)
                    {
                        lock (firstSolutionGate)
                        {
                            if (firstSolutionMilliseconds == 0L)
                            {
                                firstSolutionMilliseconds =
                                    comparisonStopwatch.ElapsedMilliseconds;
                            }
                        }
                    }

                    progress?.Report(progressUpdate);
                });

            using (var timeLimitSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
            {
                if (routeRequest.SearchOptions.EnableAutomaticTotalSearchLimit &&
                    !routeRequest.SearchOptions.PrioritizeAllSolutions)
                {
                    timeLimitSource.CancelAfter(Math.Max(
                        1,
                        routeRequest.SearchOptions
                            .TotalSearchLimitMilliseconds));
                }

                var solvers = new ILinkageMultiGroupRouteSolver[]
                {
                    new SweptRepairLinkageRouteSolver()
                };
                var primaryRouteRequest = ClonePrimarySearchRequest(
                    routeRequest);
                var solverResults = await Task.WhenAll(solvers.Select(
                    solver => Task.Run(() =>
                    {
                        try
                        {
                            return solver.Solve(
                                primaryRouteRequest,
                                primarySearchProgress,
                                timeLimitSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return (IReadOnlyList<LinkageRouteSolution>)new[]
                            {
                                CreateCancelledSolution(
                                    solver.AlgorithmKind,
                                    cancellationToken.IsCancellationRequested)
                            };
                        }
                    })).ToArray()).ConfigureAwait(false);

                var stoppedByConfiguredTimeLimit =
                    timeLimitSource.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested;
                foreach (var solverResult in solverResults)
                {
                    foreach (var routeSolution in solverResult)
                    {
                        StampPrimarySearchSolution(
                            routeSolution,
                            routeRequest.InsulationClearance,
                            routeRequest.LapInsulationClearance);
                        if (routeSolution?.IsSuccess == true &&
                            routeSolution.IsRelaxedClearanceSolution)
                        {
                            routeSolution.IsSuccess = false;
                            routeSolution.Diagnostics.Add(
                                new LinkageRouteFailureDiagnostic
                                {
                                    Stage = LinkageRouteFailureStage.Clearance,
                                    Message = "最终实际绝缘距离小于 Json 请求值；未降低绝缘距离继续搜索。"
                                });
                        }

                        if (stoppedByConfiguredTimeLimit &&
                            routeSolution.IsCancelled)
                        {
                            foreach (var cancellationDiagnostic in
                                routeSolution.Diagnostics.Where(diagnostic =>
                                    diagnostic.Stage ==
                                    LinkageRouteFailureStage.Cancelled))
                            {
                                cancellationDiagnostic.Stage =
                                    LinkageRouteFailureStage.TimeLimit;
                                cancellationDiagnostic.Message =
                                    "路径搜索达到请求设置的总时限；已完成方案仍保留。";
                            }
                        }

                        comparisonResult.RankedSolutions.Add(routeSolution);
                    }
                }

                comparisonResult.IsCancelled =
                    cancellationToken.IsCancellationRequested;
            }

            comparisonResult.ElapsedMilliseconds = comparisonStopwatch.ElapsedMilliseconds;
            comparisonResult.FirstSolutionElapsedMilliseconds = firstSolutionMilliseconds;
            var successfulTopologyRepresentatives = comparisonResult.RankedSolutions
                .Where(solution => solution?.IsSuccess == true)
                .GroupBy(ResolveTopologySignature, StringComparer.Ordinal)
                .Select(topologySolutionGroup => topologySolutionGroup.Aggregate(
                    (currentTopologyRepresentative, candidateTopologyRepresentative) =>
                        LinkageRouteTopologySignature.IsPreferredRepresentative(
                            candidateTopologyRepresentative,
                            currentTopologyRepresentative)
                                ? candidateTopologyRepresentative
                                : currentTopologyRepresentative));
            var failedSolutions = comparisonResult.RankedSolutions
                .Where(solution => solution?.IsSuccess != true);
            var rankedSolutions = successfulTopologyRepresentatives
                .Concat(failedSolutions)
                .OrderByDescending(solution => solution.IsSuccess)
                .ThenBy(solution => solution.IsSuccess &&
                    solution.IsRelaxedClearanceSolution ? 1 : 0)
                .ThenByDescending(solution => solution.IsSuccess &&
                    solution.IsRelaxedClearanceSolution
                        ? NormalizeMinimumClearance(solution.MinimumClearance)
                        : double.NegativeInfinity)
                .ThenBy(solution => solution.IsSuccess
                    ? solution.ActualCopperLength
                    : double.MaxValue)
                .ThenBy(solution => solution.IsSuccess
                    ? solution.GeneratedBusbarPartCount
                    : int.MaxValue)
                .ThenBy(solution => solution.AlgorithmKind)
                .ToList();
            var successfulSchemeIndex = 0;
            foreach (var rankedSolution in rankedSolutions)
            {
                if (rankedSolution.IsSuccess)
                {
                    rankedSolution.SchemeIndex = ++successfulSchemeIndex;
                }
            }

            comparisonResult.RankedSolutions.Clear();
            foreach (var rankedSolution in rankedSolutions)
            {
                comparisonResult.RankedSolutions.Add(rankedSolution);
            }

            return comparisonResult;
        }

        private static void StampPrimarySearchSolution(
            LinkageRouteSolution routeSolution,
            double requestedInsulationClearance,
            double requestedLapInsulationClearance)
        {
            if (routeSolution == null)
            {
                return;
            }

            routeSolution.RequestedInsulationClearance =
                requestedInsulationClearance;
            routeSolution.RequestedLapInsulationClearance =
                requestedLapInsulationClearance;
            routeSolution.SearchInsulationClearance =
                requestedInsulationClearance;
            routeSolution.IsRelaxedClearanceSolution =
                routeSolution.IsSuccess &&
                !double.IsNaN(routeSolution.MinimumClearance) &&
                routeSolution.MinimumClearance +
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                    requestedInsulationClearance;
        }

        private static LinkageMultiGroupRouteRequest ClonePrimarySearchRequest(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var primaryRouteRequest = LinkageRouteWorkerContractMapper
                .FromWorkerRequest(
                    LinkageRouteWorkerContractMapper.ToWorkerRequest(
                        routeRequest));
            primaryRouteRequest.RuntimeControl = routeRequest.RuntimeControl;
            return primaryRouteRequest;
        }

        private static string ResolveTopologySignature(
            LinkageRouteSolution routeSolution)
        {
            var topologySignature = LinkageRouteTopologySignature
                .CreateSolutionSignature(routeSolution);
            return string.IsNullOrEmpty(topologySignature)
                ? "GEOMETRY:" + LinkageRouteTopologySignature
                    .CreateStableGeometrySignature(routeSolution)
                : topologySignature;
        }

        private static double NormalizeMinimumClearance(double minimumClearance)
        {
            return double.IsNaN(minimumClearance)
                ? double.NegativeInfinity
                : minimumClearance;
        }

        private static LinkageRouteSolution CreateCancelledSolution(
            LinkageRouteAlgorithmKind algorithmKind,
            bool userCancelled)
        {
            var cancelledSolution = new LinkageRouteSolution
            {
                AlgorithmKind = algorithmKind,
                IsSuccess = false,
                IsCancelled = true
            };
            cancelledSolution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
            {
                Stage = userCancelled
                    ? LinkageRouteFailureStage.Cancelled
                    : LinkageRouteFailureStage.TimeLimit,
                Message = userCancelled ? "用户取消了路径搜索。" : "路径搜索达到请求设置的总时限。"
            });
            return cancelledSolution;
        }

        private sealed class InlineProgress<TProgress> : IProgress<TProgress>
        {
            private readonly Action<TProgress> reportAction;

            public InlineProgress(Action<TProgress> reportAction)
            {
                this.reportAction = reportAction ?? throw new ArgumentNullException(nameof(reportAction));
            }

            public void Report(TProgress value)
            {
                reportAction(value);
            }
        }
    }
}
