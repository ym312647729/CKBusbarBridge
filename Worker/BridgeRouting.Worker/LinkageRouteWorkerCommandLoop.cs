using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Routing;
using RouteLinkBar.Algorithm.Transport;

namespace RouteLinkBar.Algorithm.Worker
{
    internal sealed class LinkageRouteWorkerCommandLoop
    {
        private const long ProgressPublishIntervalMilliseconds = 200L;

        private readonly TextReader commandInputReader;
        private readonly TextWriter protocolOutputWriter;
        private readonly TextWriter diagnosticErrorWriter;
        private readonly int parentProcessId;
        private readonly LinkageRouteWorkerInformationDto workerInformation;
        private readonly object protocolWriteGate = new object();
        private readonly object searchSessionGate = new object();
        private WorkerSearchSession activeSearchSession;
        private long lastProgressPublishTimestamp;
        private bool hasAuthorizedHandshake;

        public LinkageRouteWorkerCommandLoop(
            TextReader commandInputReader,
            TextWriter protocolOutputWriter,
            TextWriter diagnosticErrorWriter,
            int parentProcessId)
        {
            this.commandInputReader = commandInputReader ??
                throw new ArgumentNullException(nameof(commandInputReader));
            this.protocolOutputWriter = protocolOutputWriter ??
                throw new ArgumentNullException(nameof(protocolOutputWriter));
            this.diagnosticErrorWriter = diagnosticErrorWriter ?? TextWriter.Null;
            this.parentProcessId = parentProcessId;
            workerInformation = LinkageRouteWorkerInformationProvider
                .GetWorkerInformation();
        }

        public int Run()
        {
            string commandJson;
            while ((commandJson = commandInputReader.ReadLine()) != null)
            {
                LinkageRouteWorkerMessageDto commandMessage;
                try
                {
                    commandMessage = LinkageRouteWorkerMessageCodec.Deserialize(commandJson);
                }
                catch (Exception protocolException)
                {
                    SendFault(null, "算法 Worker 收到无效协议消息。", protocolException);
                    continue;
                }

                var responseProtocolVersion = ResolveResponseProtocolVersion(
                    commandMessage.ProtocolVersion);

                if (string.Equals(
                    commandMessage.MessageType,
                    LinkageRouteWorkerProtocol.Handshake,
                    StringComparison.Ordinal))
                {
                    if (!ValidateAndClearAuthorizationCode(commandMessage))
                    {
                        hasAuthorizedHandshake = false;
                        SendFault(
                            commandMessage.RequestId,
                            "算法 Worker 授权失败。",
                            null,
                            responseProtocolVersion);
                        continue;
                    }

                    hasAuthorizedHandshake = true;
                    Send(new LinkageRouteWorkerMessageDto
                    {
                        ProtocolVersion = responseProtocolVersion,
                        MessageType = LinkageRouteWorkerProtocol.HandshakeAcknowledged,
                        RequestId = commandMessage.RequestId,
                        ParentProcessId = parentProcessId,
                        WorkerInformation = workerInformation
                    });
                    continue;
                }

                if (string.Equals(
                    commandMessage.MessageType,
                    LinkageRouteWorkerProtocol.SolveRequest,
                    StringComparison.Ordinal))
                {
                    var isSolveRequestAuthorized =
                        ValidateAndClearAuthorizationCode(commandMessage);
                    if (!hasAuthorizedHandshake || !isSolveRequestAuthorized)
                    {
                        SendFault(
                            commandMessage.RequestId,
                            "算法 Worker 授权失败。",
                            null,
                            responseProtocolVersion);
                        continue;
                    }

                    StartSearch(commandMessage, responseProtocolVersion);
                    continue;
                }

                if (string.Equals(
                    commandMessage.MessageType,
                    LinkageRouteWorkerProtocol.CancelRequest,
                    StringComparison.Ordinal))
                {
                    CancelSearch(
                        commandMessage.RequestId,
                        responseProtocolVersion);
                    continue;
                }

                if (string.Equals(
                    commandMessage.MessageType,
                    LinkageRouteWorkerProtocol.PrioritizeOptimizationRequest,
                    StringComparison.Ordinal))
                {
                    PrioritizeSearchOptimization(
                        commandMessage.RequestId,
                        responseProtocolVersion);
                    continue;
                }

                if (string.Equals(
                    commandMessage.MessageType,
                    LinkageRouteWorkerProtocol.Shutdown,
                    StringComparison.Ordinal))
                {
                    CancelAllSearches();
                    Send(new LinkageRouteWorkerMessageDto
                    {
                        ProtocolVersion = responseProtocolVersion,
                        MessageType = LinkageRouteWorkerProtocol.ShutdownAcknowledged,
                        RequestId = commandMessage.RequestId
                    });
                    return 0;
                }

                SendFault(
                    commandMessage.RequestId,
                    "算法 Worker 不支持消息类型：" + commandMessage.MessageType,
                    null,
                    responseProtocolVersion);
            }

            // stdin EOF 表示所属窗口或 SolidWorks 宿主已经释放通信管道。
            CancelAllSearches();
            return 0;
        }

        private void StartSearch(
            LinkageRouteWorkerMessageDto solveMessage,
            int responseProtocolVersion)
        {
            if (string.IsNullOrWhiteSpace(solveMessage.RequestId) || solveMessage.Request == null)
            {
                SendFault(
                    solveMessage.RequestId,
                    "求解请求缺少 RequestId 或完整算法参数。",
                    null,
                    responseProtocolVersion);
                return;
            }

            WorkerSearchSession newSearchSession;
            lock (searchSessionGate)
            {
                if (activeSearchSession != null && !activeSearchSession.SearchTask.IsCompleted)
                {
                    SendFault(
                        solveMessage.RequestId,
                        "上一轮算法搜索尚未停止，不能并行启动第二轮搜索。",
                        null,
                        responseProtocolVersion);
                    return;
                }

                var searchCancellationSource = new CancellationTokenSource();
                newSearchSession = new WorkerSearchSession(
                    solveMessage.RequestId,
                    searchCancellationSource,
                    new LinkageRouteSearchRuntimeControl(),
                    responseProtocolVersion);
                activeSearchSession = newSearchSession;
                lastProgressPublishTimestamp = 0L;
                newSearchSession.SearchTask = StartDedicatedWorkerTask(() => ExecuteSearch(
                    solveMessage,
                    newSearchSession));
            }
        }

        private void ExecuteSearch(
            LinkageRouteWorkerMessageDto solveMessage,
            WorkerSearchSession searchSession)
        {
            try
            {
                var routeRequest = LinkageRouteWorkerContractMapper.FromWorkerRequest(
                    solveMessage.Request);
                routeRequest.Validate();
                if (!routeRequest.RequireAxisAlignedRoutes)
                    throw new InvalidOperationException("母线桥 Worker 仅接受轴向母线桥计算请求。");
                var routeProgress = new InlineProgress<LinkageRouteProgress>(progressUpdate =>
                    PublishProgress(
                        searchSession.RequestId,
                        searchSession.ResponseProtocolVersion,
                        progressUpdate));
                // 方案库只消费命中记录的逐排历史模板，不依赖普通搜索方案池。
                // 同时开启两个开关时仍独立并行发布两种来源的完整方案。
                var ordinarySearchRequired = routeRequest.EnableAlgorithmCalculation;
                var ordinaryProgress = new InlineProgress<LinkageRouteProgress>(progressUpdate =>
                {
                    if (routeRequest.EnableAlgorithmCalculation)
                    {
                        routeProgress.Report(progressUpdate);
                    }
                });
                var comparisonTask = ordinarySearchRequired
                    ? StartDedicatedWorkerTask<LinkageRouteComparisonResult>(() =>
                        LinkageMultiGroupRouteComparisonRunner.RunAsync(
                            routeRequest,
                            ordinaryProgress,
                            searchSession.RuntimeControl,
                            searchSession.CancellationSource.Token)
                        .GetAwaiter()
                        .GetResult())
                    : Task.FromResult(new LinkageRouteComparisonResult());
                var comparisonResult = comparisonTask.GetAwaiter().GetResult();
                Send(new LinkageRouteWorkerMessageDto
                {
                    ProtocolVersion = searchSession.ResponseProtocolVersion,
                    MessageType = LinkageRouteWorkerProtocol.Completed,
                    RequestId = searchSession.RequestId,
                    Result = LinkageRouteWorkerContractMapper.ToWorkerComparisonResult(
                        routeRequest.EnableAlgorithmCalculation
                            ? comparisonResult
                            : new LinkageRouteComparisonResult())
                });
            }
            catch (OperationCanceledException)
            {
                var cancelledResult = new LinkageRouteComparisonResult
                {
                    IsCancelled = true
                };
                Send(new LinkageRouteWorkerMessageDto
                {
                    ProtocolVersion = searchSession.ResponseProtocolVersion,
                    MessageType = LinkageRouteWorkerProtocol.Completed,
                    RequestId = searchSession.RequestId,
                    Result = LinkageRouteWorkerContractMapper.ToWorkerComparisonResult(
                        cancelledResult)
                });
            }
            catch (Exception searchException)
            {
                SendFault(
                    searchSession.RequestId,
                    "独立算法进程求解失败：" + searchException.Message,
                    searchException,
                    searchSession.ResponseProtocolVersion);
            }
            finally
            {
                lock (searchSessionGate)
                {
                    if (ReferenceEquals(activeSearchSession, searchSession))
                    {
                        activeSearchSession = null;
                    }
                }

                searchSession.CancellationSource.Dispose();
                searchSession.RuntimeControl.Dispose();
            }
        }

        private static Task StartDedicatedWorkerTask(Action workerAction)
        {
            if (workerAction == null)
            {
                throw new ArgumentNullException(nameof(workerAction));
            }

            return Task.Factory.StartNew(
                workerAction,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private static Task<TResult> StartDedicatedWorkerTask<TResult>(
            Func<TResult> workerFunction)
        {
            if (workerFunction == null)
            {
                throw new ArgumentNullException(nameof(workerFunction));
            }

            return Task.Factory.StartNew(
                workerFunction,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private void PrioritizeSearchOptimization(
            string requestId,
            int responseProtocolVersion)
        {
            WorkerSearchSession searchSession;
            lock (searchSessionGate)
            {
                searchSession = activeSearchSession;
            }

            var commandAccepted = searchSession != null &&
                string.Equals(
                    searchSession.RequestId,
                    requestId,
                    StringComparison.Ordinal) &&
                searchSession.RuntimeControl.RequestOptimizationPriority();
            Send(new LinkageRouteWorkerMessageDto
            {
                ProtocolVersion = responseProtocolVersion,
                MessageType =
                    LinkageRouteWorkerProtocol.PrioritizeOptimizationAcknowledged,
                RequestId = requestId,
                ErrorMessage = commandAccepted
                    ? null
                    : "当前求解已结束或已经切换为优先优化方案。"
            });
        }

        private void PublishProgress(
            string requestId,
            int responseProtocolVersion,
            LinkageRouteProgress progressUpdate)
        {
            if (progressUpdate == null)
            {
                return;
            }

            if (progressUpdate.PublishedSolution?.IsSuccess == true)
            {
                Send(new LinkageRouteWorkerMessageDto
                {
                    ProtocolVersion = responseProtocolVersion,
                    MessageType = LinkageRouteWorkerProtocol.SolutionPublished,
                    RequestId = requestId,
                    Progress = LinkageRouteWorkerContractMapper.ToWorkerProgress(progressUpdate),
                    Solution = LinkageRouteWorkerContractMapper.ToWorkerSolution(
                        progressUpdate.PublishedSolution)
                });
            }

            var currentTimestamp = Stopwatch.GetTimestamp();
            var elapsedSinceLastPublishMilliseconds = lastProgressPublishTimestamp == 0L
                ? long.MaxValue
                : (currentTimestamp - lastProgressPublishTimestamp) * 1000L /
                  Stopwatch.Frequency;
            if (elapsedSinceLastPublishMilliseconds < ProgressPublishIntervalMilliseconds &&
                progressUpdate.PublishedSolution == null)
            {
                return;
            }

            lastProgressPublishTimestamp = currentTimestamp;
            Send(new LinkageRouteWorkerMessageDto
            {
                ProtocolVersion = responseProtocolVersion,
                MessageType = LinkageRouteWorkerProtocol.Progress,
                RequestId = requestId,
                Progress = LinkageRouteWorkerContractMapper.ToWorkerProgress(progressUpdate)
            });
        }

        private void CancelSearch(
            string requestId,
            int responseProtocolVersion)
        {
            WorkerSearchSession searchSession;
            lock (searchSessionGate)
            {
                searchSession = activeSearchSession;
            }

            if (searchSession != null &&
                string.Equals(searchSession.RequestId, requestId, StringComparison.Ordinal))
            {
                try
                {
                    searchSession.CancellationSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 搜索可能恰好自然完成。
                }
            }

            Send(new LinkageRouteWorkerMessageDto
            {
                ProtocolVersion = responseProtocolVersion,
                MessageType = LinkageRouteWorkerProtocol.CancelAcknowledged,
                RequestId = requestId
            });
        }

        private void CancelAllSearches()
        {
            WorkerSearchSession searchSession;
            lock (searchSessionGate)
            {
                searchSession = activeSearchSession;
            }

            try
            {
                searchSession?.CancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 搜索已经结束。
            }
        }

        private static int ResolveResponseProtocolVersion(
            int requestedProtocolVersion)
        {
            return requestedProtocolVersion > 0
                ? requestedProtocolVersion
                : LinkageRouteWorkerProtocol.CurrentVersion;
        }

        private static bool ValidateAndClearAuthorizationCode(
            LinkageRouteWorkerMessageDto workerMessage)
        {
            if (workerMessage == null)
            {
                return false;
            }

            var providedAuthorizationCode = workerMessage.AuthorizationCode;
            workerMessage.AuthorizationCode = null;
            return LinkageRouteWorkerAuthorizationValidator.IsAuthorized(
                providedAuthorizationCode);
        }

        private void SendFault(
            string requestId,
            string errorMessage,
            Exception exception,
            int responseProtocolVersion = LinkageRouteWorkerProtocol.CurrentVersion)
        {
            diagnosticErrorWriter.WriteLine(errorMessage);
            if (exception != null)
            {
                diagnosticErrorWriter.WriteLine(exception);
            }

            Send(new LinkageRouteWorkerMessageDto
            {
                ProtocolVersion = responseProtocolVersion,
                MessageType = LinkageRouteWorkerProtocol.Faulted,
                RequestId = requestId,
                ErrorMessage = errorMessage,
                ErrorDetails = exception?.ToString()
            });
        }

        private void Send(LinkageRouteWorkerMessageDto workerMessage)
        {
            var messageJson = LinkageRouteWorkerMessageCodec.Serialize(workerMessage);
            lock (protocolWriteGate)
            {
                protocolOutputWriter.WriteLine(messageJson);
                protocolOutputWriter.Flush();
            }
        }

        private sealed class WorkerSearchSession
        {
            public WorkerSearchSession(
                string requestId,
                CancellationTokenSource cancellationSource,
                LinkageRouteSearchRuntimeControl runtimeControl,
                int responseProtocolVersion)
            {
                RequestId = requestId;
                CancellationSource = cancellationSource;
                RuntimeControl = runtimeControl;
                ResponseProtocolVersion = responseProtocolVersion;
                SearchTask = Task.CompletedTask;
            }

            public string RequestId { get; }

            public CancellationTokenSource CancellationSource { get; }

            public LinkageRouteSearchRuntimeControl RuntimeControl { get; }

            public int ResponseProtocolVersion { get; }

            public Task SearchTask { get; set; }
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
