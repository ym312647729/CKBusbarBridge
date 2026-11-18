using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridgePreview;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CKBusbarBridge.Services
{
    /// <summary>
    /// 管理母排设置窗口独占的算法 Worker。只回收本实例启动并记录的进程 PID。
    /// </summary>
    internal sealed class BusbarAlgorithmWorkerClient : IDisposable
    {
        private const int HandshakeTimeoutMilliseconds = 5000;
        private const int CancellationTimeoutMilliseconds = 2000;
        private const int ShutdownTimeoutMilliseconds = 1500;
        private const string ProjectWorkerRequestFileName = "WorkerRequest.json";

        private readonly Func<string> _resolveAuthorization;
        internal BusbarAlgorithmWorkerClient(Func<string> resolveAuthorization = null)
        {
            _resolveAuthorization = resolveAuthorization;
        }

        private readonly object _workerStateGate = new object();
        private readonly object _protocolWriteGate = new object();
        private readonly SemaphoreSlim _workerStartupGate = new SemaphoreSlim(1, 1);
        private Process _ownedWorkerProcess;
        private StreamWriter _workerCommandWriter;
        private string _workerAuthorizationCode;
        private TaskCompletionSource<bool> _handshakeCompletionSource;
        private string _handshakeRequestId;
        private TaskCompletionSource<bool> _shutdownCompletionSource;
        private string _shutdownRequestId;
        private WorkerSearchSession _activeSearchSession;
        private int _workerGeneration;
        private bool _isDisposed;

        public string WorkerExecutablePath => BusbarAlgorithmWorkerLocator.ResolveWorkerExecutablePath();

        public Task<LinkageRouteWorkerComparisonResultDto> SolveAsync(
            LinkageRouteWorkerRequestDto workerRequest,
            IProgress<BusbarAlgorithmWorkerUpdate> workerProgress,
            CancellationToken cancellationToken) =>
            SolveProjectAsync(workerRequest, workerProgress, cancellationToken, null);

        internal async Task<LinkageRouteWorkerComparisonResultDto> SolveProjectAsync(
            LinkageRouteWorkerRequestDto workerRequest,
            IProgress<BusbarAlgorithmWorkerUpdate> workerProgress,
            CancellationToken cancellationToken,
            string projectAssemblyPath)
        {
            if (workerRequest == null)
            {
                throw new ArgumentNullException(nameof(workerRequest));
            }

            if (!workerRequest.RequireAxisAlignedRoutes)
            {
                throw new InvalidOperationException(
                    "母排设置入口只允许全局 XYZ 轴向方案，Worker 请求未锁定该约束。");
            }

            ThrowIfDisposed();
            await CancelActiveSearchAsync().ConfigureAwait(false);
            using (CKBusbarBridge.Computation.CalculationConcurrency.BeginRouting())
            {
                workerRequest.SearchOptions.MaximumDegreeOfParallelism = Math.Max(1, Math.Min(
                    workerRequest.SearchOptions.MaximumDegreeOfParallelism,
                    CKBusbarBridge.Computation.CalculationConcurrency.RoutingWorkers));
                for (var workerStartAttemptIndex = 0;
                     workerStartAttemptIndex < 2;
                     workerStartAttemptIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await SolveOnceAsync(
                                workerRequest,
                                workerProgress,
                                cancellationToken,
                                projectAssemblyPath)
                            .ConfigureAwait(false);
                    }
                    catch (BusbarAlgorithmWorkerUnavailableException)
                        when (workerStartAttemptIndex == 0 &&
                              !cancellationToken.IsCancellationRequested)
                    {
                        StopOwnedWorkerImmediately();
                    }
                }

                throw new BusbarAlgorithmWorkerUnavailableException(
                    "独立算法进程重启后仍无法完成搜索。");
            }
        }

        public Task CancelActiveSearchAsync()
        {
            WorkerSearchSession searchSessionToCancel;
            lock (_workerStateGate)
            {
                searchSessionToCancel = _activeSearchSession;
            }

            return searchSessionToCancel == null
                ? Task.CompletedTask
                : CancelSearchSessionAsync(searchSessionToCancel);
        }

        public void Dispose()
        {
            Process workerProcessToStop;
            StreamWriter workerWriterToClose;
            WorkerSearchSession searchSessionToCancel;
            TaskCompletionSource<bool> handshakeToCancel;
            TaskCompletionSource<bool> shutdownToWait;
            string shutdownRequestIdentifier;
            int ownedWorkerGeneration;

            lock (_workerStateGate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                workerProcessToStop = _ownedWorkerProcess;
                workerWriterToClose = _workerCommandWriter;
                searchSessionToCancel = _activeSearchSession;
                handshakeToCancel = _handshakeCompletionSource;
                ownedWorkerGeneration = _workerGeneration;
                shutdownRequestIdentifier = Guid.NewGuid().ToString("N");
                shutdownToWait = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownRequestId = shutdownRequestIdentifier;
                _shutdownCompletionSource = shutdownToWait;
                _activeSearchSession = null;
                _workerAuthorizationCode = null;
                _handshakeCompletionSource = null;
                _handshakeRequestId = null;
            }

            searchSessionToCancel?.CompletionSource.TrySetCanceled();
            handshakeToCancel?.TrySetCanceled();
            if (workerProcessToStop == null)
            {
                return;
            }

            try
            {
                if (searchSessionToCancel != null)
                {
                    WriteMessageDirectly(
                        workerWriterToClose,
                        new LinkageRouteWorkerMessageDto
                        {
                            MessageType = LinkageRouteWorkerProtocol.CancelRequest,
                            RequestId = searchSessionToCancel.RequestId
                        });
                }

                WriteMessageDirectly(
                    workerWriterToClose,
                    new LinkageRouteWorkerMessageDto
                    {
                        MessageType = LinkageRouteWorkerProtocol.Shutdown,
                        RequestId = shutdownRequestIdentifier
                    });

                try
                {
                    shutdownToWait.Task.Wait(ShutdownTimeoutMilliseconds);
                }
                catch (AggregateException)
                {
                    // Worker 已退出或管道断开时继续按当前 PID 回收。
                }
            }
            catch (IOException)
            {
                // 管道已经断开，下面只回收本窗口记录的进程。
            }
            catch (ObjectDisposedException)
            {
                // Worker 已自然退出。
            }
            finally
            {
                try
                {
                    workerWriterToClose?.Close();
                }
                catch (IOException)
                {
                    // 关闭管道时 Worker 可能已经退出。
                }
            }

            WaitForExitAndKillOwnedProcess(
                workerProcessToStop,
                ownedWorkerGeneration,
                ShutdownTimeoutMilliseconds);

            lock (_workerStateGate)
            {
                if (ReferenceEquals(_ownedWorkerProcess, workerProcessToStop))
                {
                    _ownedWorkerProcess = null;
                    _workerCommandWriter = null;
                }

                _shutdownCompletionSource = null;
                _shutdownRequestId = null;
            }
        }

        private async Task<LinkageRouteWorkerComparisonResultDto> SolveOnceAsync(
            LinkageRouteWorkerRequestDto workerRequest,
            IProgress<BusbarAlgorithmWorkerUpdate> workerProgress,
            CancellationToken cancellationToken,
            string projectAssemblyPath)
        {
            var solveRequestIdentifier = Guid.NewGuid().ToString("N");
            TrySaveProjectWorkerRequest(projectAssemblyPath, workerRequest, solveRequestIdentifier);
            await EnsureWorkerStartedAsync().ConfigureAwait(false);

            WorkerSearchSession createdSearchSession;
            string activeWorkerAuthorizationCode;
            lock (_workerStateGate)
            {
                ThrowIfDisposed();
                if (_ownedWorkerProcess == null || _ownedWorkerProcess.HasExited)
                {
                    throw new BusbarAlgorithmWorkerUnavailableException(
                        "独立算法进程在发送请求前已经退出。");
                }

                activeWorkerAuthorizationCode = _workerAuthorizationCode;
                if (string.IsNullOrWhiteSpace(activeWorkerAuthorizationCode))
                {
                    throw new BusbarAlgorithmWorkerAuthorizationException(
                        "独立算法进程授权会话已经失效。");
                }

                createdSearchSession = new WorkerSearchSession(
                    solveRequestIdentifier,
                    _workerGeneration,
                    workerProgress);
                _activeSearchSession = createdSearchSession;
            }

            using (cancellationToken.Register(() =>
                ThreadPool.QueueUserWorkItem(ignoredState =>
                {
                    try
                    {
                        CancelSearchSessionAsync(createdSearchSession).GetAwaiter().GetResult();
                    }
                    catch (Exception cancellationException)
                    {
                        Debug.WriteLine(
                            "取消独立算法搜索失败：" + cancellationException.Message);
                    }
                })))
            {
                try
                {
                    SendMessage(
                        new LinkageRouteWorkerMessageDto
                        {
                            MessageType = LinkageRouteWorkerProtocol.SolveRequest,
                            RequestId = solveRequestIdentifier,
                            AuthorizationCode = activeWorkerAuthorizationCode,
                            Request = workerRequest
                        },
                        createdSearchSession.WorkerGeneration);
                    return await createdSearchSession.CompletionSource.Task
                        .ConfigureAwait(false);
                }
                finally
                {
                    ClearActiveSession(createdSearchSession);
                }
            }
        }

        private static void TrySaveProjectWorkerRequest(
            string projectAssemblyPath,
            LinkageRouteWorkerRequestDto workerRequest,
            string requestIdentifier)
        {
            if (string.IsNullOrWhiteSpace(projectAssemblyPath))
            {
                return;
            }

            string temporaryRequestPath = null;
            try
            {
                // 已保存的总装位于客户项目根目录。无绝对路径时不写到插件目录。
                if (!string.Equals(Path.GetExtension(projectAssemblyPath), ".SLDASM", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath(projectAssemblyPath), projectAssemblyPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var projectDirectory = Path.GetDirectoryName(projectAssemblyPath);
                var snapshotPath = Path.Combine(projectDirectory, ProjectWorkerRequestFileName);
                // 使用发送端同一编解码器保存完整请求字段；不写入授权码。
                var requestJson = LinkageRouteWorkerMessageCodec.Serialize(new LinkageRouteWorkerMessageDto
                {
                    MessageType = LinkageRouteWorkerProtocol.SolveRequest,
                    RequestId = requestIdentifier,
                    Request = workerRequest
                });
                var stagingPath = Path.Combine(projectDirectory, ".WorkerRequest-" + requestIdentifier + ".tmp");
                using (var snapshotStream = new FileStream(
                    stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryRequestPath = stagingPath;
                    using (var snapshotWriter = new StreamWriter(snapshotStream, new UTF8Encoding(false)))
                    {
                        snapshotWriter.Write(requestJson);
                    }
                }

                try
                {
                    if (!File.Exists(snapshotPath))
                    {
                        File.Move(temporaryRequestPath, snapshotPath);
                        temporaryRequestPath = null;
                        return;
                    }
                }
                catch (IOException) when (File.Exists(snapshotPath))
                {
                    // 同一项目的另一个窗口刚创建了固定文件，改用原子覆盖。
                }

                File.Replace(temporaryRequestPath, snapshotPath, null);
                temporaryRequestPath = null;
            }
            catch (Exception exportError) when (exportError is IOException ||
                exportError is UnauthorizedAccessException || exportError is ArgumentException ||
                exportError is NotSupportedException || exportError is SerializationException ||
                exportError is System.Security.SecurityException)
            {
                FaceToWallPreviewTrace.Error("项目 Worker 请求导出失败", exportError);
            }
            finally
            {
                if (temporaryRequestPath != null)
                {
                    try { File.Delete(temporaryRequestPath); }
                    catch (IOException cleanupError) { FaceToWallPreviewTrace.Error("清理 Worker 请求临时文件失败", cleanupError); }
                    catch (UnauthorizedAccessException cleanupError) { FaceToWallPreviewTrace.Error("清理 Worker 请求临时文件失败", cleanupError); }
                }
            }
        }

        private async Task EnsureWorkerStartedAsync()
        {
            await _workerStartupGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                lock (_workerStateGate)
                {
                    if (_ownedWorkerProcess != null && !_ownedWorkerProcess.HasExited &&
                        _handshakeCompletionSource?.Task.Status == TaskStatus.RanToCompletion)
                    {
                        return;
                    }
                }

                StopOwnedWorkerImmediately();
                StartWorkerProcess();

                Task handshakeTask;
                lock (_workerStateGate)
                {
                    handshakeTask = _handshakeCompletionSource.Task;
                }

                var completedHandshakeTask = await Task.WhenAny(
                        handshakeTask,
                        Task.Delay(HandshakeTimeoutMilliseconds))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(completedHandshakeTask, handshakeTask))
                {
                    StopOwnedWorkerImmediately();
                    throw new BusbarAlgorithmWorkerUnavailableException(
                        "独立算法进程启动超时，未完成协议握手。");
                }

                try
                {
                    await handshakeTask.ConfigureAwait(false);
                }
                catch
                {
                    StopOwnedWorkerImmediately();
                    throw;
                }
            }
            finally
            {
                _workerStartupGate.Release();
            }
        }

        private void StartWorkerProcess()
        {
            var workerExecutablePath = WorkerExecutablePath;
            if (!File.Exists(workerExecutablePath))
            {
                throw new BusbarAlgorithmWorkerUnavailableException(
                    "未找到独立算法程序：" + workerExecutablePath +
                    "。请整体复制 AlgorithmWorker 目录后重试。");
            }

            var newWorkerGeneration = Interlocked.Increment(ref _workerGeneration);
            var handshakeIdentifier = Guid.NewGuid().ToString("N");
            var currentHostProcessIdentifier = GetCurrentProcessId();
            var resolvedWorkerAuthorizationCode = (_resolveAuthorization ?? throw new BusbarAlgorithmWorkerAuthorizationException("尚未连接算法授权服务。"))();
            var workerStartInfo = new ProcessStartInfo
            {
                FileName = workerExecutablePath,
                Arguments = "--parent-pid " + currentHostProcessIdentifier,
                WorkingDirectory = Path.GetDirectoryName(workerExecutablePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            var startedWorkerProcess = new Process
            {
                StartInfo = workerStartInfo,
                EnableRaisingEvents = true
            };

            startedWorkerProcess.Exited += (eventSender, eventArgs) =>
                HandleWorkerUnavailable(
                    startedWorkerProcess,
                    newWorkerGeneration,
                    "独立算法进程已退出。",
                    null);
            startedWorkerProcess.ErrorDataReceived += (eventSender, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Debug.WriteLine("[AlgorithmWorker] " + eventArgs.Data);
                }
            };

            try
            {
                if (!startedWorkerProcess.Start())
                {
                    throw new InvalidOperationException("Process.Start 返回 false。");
                }

                var commandWriter = new StreamWriter(
                    startedWorkerProcess.StandardInput.BaseStream,
                    new UTF8Encoding(false),
                    4096,
                    false)
                {
                    AutoFlush = true
                };
                var newHandshakeCompletionSource =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_workerStateGate)
                {
                    _ownedWorkerProcess = startedWorkerProcess;
                    _workerCommandWriter = commandWriter;
                    _workerAuthorizationCode = resolvedWorkerAuthorizationCode;
                    _handshakeRequestId = handshakeIdentifier;
                    _handshakeCompletionSource = newHandshakeCompletionSource;
                }

                startedWorkerProcess.BeginErrorReadLine();
                Task.Run(() => ReadProtocolLoopAsync(
                    startedWorkerProcess,
                    newWorkerGeneration));
                SendMessage(
                    new LinkageRouteWorkerMessageDto
                    {
                        MessageType = LinkageRouteWorkerProtocol.Handshake,
                        RequestId = handshakeIdentifier,
                        ParentProcessId = currentHostProcessIdentifier,
                        AuthorizationCode = resolvedWorkerAuthorizationCode
                    },
                    newWorkerGeneration);
            }
            catch (Exception workerStartException)
            {
                TryKillOwnedProcess(startedWorkerProcess);
                startedWorkerProcess.Dispose();
                throw new BusbarAlgorithmWorkerUnavailableException(
                    "启动独立算法进程失败：" + workerStartException.Message,
                    workerStartException);
            }
        }

        private async Task ReadProtocolLoopAsync(
            Process protocolWorkerProcess,
            int protocolWorkerGeneration)
        {
            try
            {
                string protocolLine;
                while ((protocolLine = await protocolWorkerProcess.StandardOutput
                    .ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    HandleWorkerMessage(
                        LinkageRouteWorkerMessageCodec.Deserialize(protocolLine),
                        protocolWorkerGeneration);
                }

                HandleWorkerUnavailable(
                    protocolWorkerProcess,
                    protocolWorkerGeneration,
                    "独立算法进程通信已断开。",
                    null);
            }
            catch (Exception protocolReadException)
            {
                HandleWorkerUnavailable(
                    protocolWorkerProcess,
                    protocolWorkerGeneration,
                    "读取独立算法进程消息失败：" + protocolReadException.Message,
                    protocolReadException);
            }
        }

        private void HandleWorkerMessage(
            LinkageRouteWorkerMessageDto workerMessage,
            int protocolWorkerGeneration)
        {
            if (workerMessage.ProtocolVersion != LinkageRouteWorkerProtocol.CurrentVersion)
            {
                throw new InvalidDataException(string.Format(
                    "独立算法协议版本不一致：界面={0}，Worker={1}。请同步协议 DTO 后再替换 Worker。",
                    LinkageRouteWorkerProtocol.CurrentVersion,
                    workerMessage.ProtocolVersion));
            }

            TaskCompletionSource<bool> currentHandshakeCompletionSource;
            string currentHandshakeRequestId;
            TaskCompletionSource<bool> currentShutdownCompletionSource;
            string currentShutdownRequestId;
            WorkerSearchSession currentSearchSession;
            lock (_workerStateGate)
            {
                if (protocolWorkerGeneration != _workerGeneration)
                {
                    return;
                }

                currentHandshakeCompletionSource = _handshakeCompletionSource;
                currentHandshakeRequestId = _handshakeRequestId;
                currentShutdownCompletionSource = _shutdownCompletionSource;
                currentShutdownRequestId = _shutdownRequestId;
                currentSearchSession = _activeSearchSession;
            }

            if (string.Equals(
                    workerMessage.MessageType,
                    LinkageRouteWorkerProtocol.Faulted,
                    StringComparison.Ordinal) &&
                string.Equals(
                    workerMessage.RequestId,
                    currentHandshakeRequestId,
                    StringComparison.Ordinal))
            {
                currentHandshakeCompletionSource?.TrySetException(
                    new BusbarAlgorithmWorkerAuthorizationException(
                        workerMessage.ErrorMessage ?? "独立算法进程授权失败。"));
                return;
            }

            if (string.Equals(
                    workerMessage.MessageType,
                    LinkageRouteWorkerProtocol.HandshakeAcknowledged,
                    StringComparison.Ordinal) &&
                string.Equals(
                    workerMessage.RequestId,
                    currentHandshakeRequestId,
                    StringComparison.Ordinal))
            {
                if (workerMessage.ParentProcessId != GetCurrentProcessId())
                {
                    currentHandshakeCompletionSource?.TrySetException(
                        new InvalidDataException("算法 Worker 握手返回的父进程 PID 不一致。"));
                    return;
                }

                currentHandshakeCompletionSource?.TrySetResult(true);
                return;
            }

            if (string.Equals(
                    workerMessage.MessageType,
                    LinkageRouteWorkerProtocol.ShutdownAcknowledged,
                    StringComparison.Ordinal) &&
                string.Equals(
                    workerMessage.RequestId,
                    currentShutdownRequestId,
                    StringComparison.Ordinal))
            {
                currentShutdownCompletionSource?.TrySetResult(true);
                return;
            }

            if (currentSearchSession == null ||
                currentSearchSession.WorkerGeneration != protocolWorkerGeneration ||
                !string.Equals(
                    currentSearchSession.RequestId,
                    workerMessage.RequestId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                LinkageRouteWorkerProtocol.Progress,
                StringComparison.Ordinal))
            {
                currentSearchSession.Progress?.Report(
                    new BusbarAlgorithmWorkerUpdate(workerMessage.Progress, null));
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                LinkageRouteWorkerProtocol.SolutionPublished,
                StringComparison.Ordinal))
            {
                currentSearchSession.Progress?.Report(
                    new BusbarAlgorithmWorkerUpdate(
                        workerMessage.Progress,
                        workerMessage.Solution));
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                LinkageRouteWorkerProtocol.CancelAcknowledged,
                StringComparison.Ordinal))
            {
                currentSearchSession.CancelAcknowledgedSource.TrySetResult(true);
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                LinkageRouteWorkerProtocol.Completed,
                StringComparison.Ordinal))
            {
                currentSearchSession.CompletionSource.TrySetResult(
                    workerMessage.Result ?? new LinkageRouteWorkerComparisonResultDto());
                ClearActiveSession(currentSearchSession);
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                LinkageRouteWorkerProtocol.Faulted,
                StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(workerMessage.ErrorDetails))
                {
                    Debug.WriteLine("[AlgorithmWorker] " + workerMessage.ErrorDetails);
                }

                currentSearchSession.CompletionSource.TrySetException(
                    new BusbarAlgorithmWorkerSearchException(
                        workerMessage.ErrorMessage ?? "独立算法进程求解失败。"));
                ClearActiveSession(currentSearchSession);
            }
        }

        private async Task CancelSearchSessionAsync(WorkerSearchSession searchSession)
        {
            if (searchSession == null || searchSession.CompletionSource.Task.IsCompleted)
            {
                return;
            }

            try
            {
                SendMessage(
                    new LinkageRouteWorkerMessageDto
                    {
                        MessageType = LinkageRouteWorkerProtocol.CancelRequest,
                        RequestId = searchSession.RequestId
                    },
                    searchSession.WorkerGeneration);
            }
            catch (BusbarAlgorithmWorkerUnavailableException)
            {
                searchSession.CompletionSource.TrySetCanceled();
                return;
            }

            var completedSearchTask = await Task.WhenAny(
                    searchSession.CompletionSource.Task,
                    Task.Delay(CancellationTimeoutMilliseconds))
                .ConfigureAwait(false);
            if (ReferenceEquals(completedSearchTask, searchSession.CompletionSource.Task))
            {
                return;
            }

            searchSession.CompletionSource.TrySetCanceled();
            StopOwnedWorkerImmediately(searchSession.WorkerGeneration);
        }

        private void SendMessage(
            LinkageRouteWorkerMessageDto workerMessage,
            int requiredWorkerGeneration)
        {
            StreamWriter commandWriter;
            Process workerProcess;
            lock (_workerStateGate)
            {
                if (_isDisposed || requiredWorkerGeneration != _workerGeneration)
                {
                    throw new BusbarAlgorithmWorkerUnavailableException(
                        "独立算法进程会话已经失效。");
                }

                commandWriter = _workerCommandWriter;
                workerProcess = _ownedWorkerProcess;
            }

            if (commandWriter == null || workerProcess == null || workerProcess.HasExited)
            {
                throw new BusbarAlgorithmWorkerUnavailableException(
                    "独立算法进程未运行或通信管道不可用。");
            }

            try
            {
                WriteMessageDirectly(commandWriter, workerMessage);
            }
            catch (Exception protocolWriteException) when (
                protocolWriteException is IOException ||
                protocolWriteException is ObjectDisposedException ||
                protocolWriteException is InvalidOperationException)
            {
                throw new BusbarAlgorithmWorkerUnavailableException(
                    "向独立算法进程发送消息失败：" + protocolWriteException.Message,
                    protocolWriteException);
            }
        }

        private void WriteMessageDirectly(
            StreamWriter commandWriter,
            LinkageRouteWorkerMessageDto workerMessage)
        {
            if (commandWriter == null)
            {
                return;
            }

            var workerMessageJson = LinkageRouteWorkerMessageCodec.Serialize(workerMessage);
            lock (_protocolWriteGate)
            {
                commandWriter.WriteLine(workerMessageJson);
                commandWriter.Flush();
            }
        }

        private void HandleWorkerUnavailable(
            Process unavailableWorkerProcess,
            int unavailableWorkerGeneration,
            string unavailableMessage,
            Exception unavailableException)
        {
            WorkerSearchSession searchSession;
            TaskCompletionSource<bool> pendingHandshake;
            TaskCompletionSource<bool> pendingShutdown;
            lock (_workerStateGate)
            {
                if (unavailableWorkerGeneration != _workerGeneration ||
                    !ReferenceEquals(_ownedWorkerProcess, unavailableWorkerProcess))
                {
                    return;
                }

                _ownedWorkerProcess = null;
                _workerCommandWriter = null;
                _workerAuthorizationCode = null;
                searchSession = _activeSearchSession;
                _activeSearchSession = null;
                pendingHandshake = _handshakeCompletionSource;
                _handshakeCompletionSource = null;
                _handshakeRequestId = null;
                pendingShutdown = _shutdownCompletionSource;
                _shutdownCompletionSource = null;
                _shutdownRequestId = null;
            }

            var unavailableExceptionWrapper =
                new BusbarAlgorithmWorkerUnavailableException(
                    unavailableMessage,
                    unavailableException);
            pendingHandshake?.TrySetException(unavailableExceptionWrapper);
            pendingShutdown?.TrySetResult(true);
            searchSession?.CompletionSource.TrySetException(unavailableExceptionWrapper);
        }

        private void StopOwnedWorkerImmediately(int? requiredWorkerGeneration = null)
        {
            Process processToStop;
            StreamWriter writerToClose;
            WorkerSearchSession searchSession;
            TaskCompletionSource<bool> pendingHandshake;
            int stoppedWorkerGeneration;

            lock (_workerStateGate)
            {
                if (requiredWorkerGeneration.HasValue &&
                    requiredWorkerGeneration.Value != _workerGeneration)
                {
                    return;
                }

                processToStop = _ownedWorkerProcess;
                writerToClose = _workerCommandWriter;
                searchSession = _activeSearchSession;
                pendingHandshake = _handshakeCompletionSource;
                stoppedWorkerGeneration = _workerGeneration;
                _ownedWorkerProcess = null;
                _workerCommandWriter = null;
                _workerAuthorizationCode = null;
                _activeSearchSession = null;
                _handshakeCompletionSource = null;
                _handshakeRequestId = null;
            }

            searchSession?.CompletionSource.TrySetCanceled();
            pendingHandshake?.TrySetException(
                new BusbarAlgorithmWorkerUnavailableException(
                    "独立算法进程已被回收。"));

            try
            {
                writerToClose?.Close();
            }
            catch (IOException)
            {
                // 管道已经关闭。
            }

            if (processToStop != null)
            {
                TryKillOwnedProcess(processToStop);
                processToStop.Dispose();
            }

            Debug.WriteLine("已回收算法 Worker 会话代次：" + stoppedWorkerGeneration);
        }

        private void ClearActiveSession(WorkerSearchSession searchSession)
        {
            lock (_workerStateGate)
            {
                if (ReferenceEquals(_activeSearchSession, searchSession))
                {
                    _activeSearchSession = null;
                }
            }
        }

        private static int GetCurrentProcessId()
        {
            using (var currentHostProcess = Process.GetCurrentProcess())
            {
                return currentHostProcess.Id;
            }
        }

        private static void WaitForExitAndKillOwnedProcess(
            Process ownedWorkerProcess,
            int ownedWorkerGeneration,
            int timeoutMilliseconds)
        {
            try
            {
                if (!ownedWorkerProcess.HasExited &&
                    !ownedWorkerProcess.WaitForExit(timeoutMilliseconds))
                {
                    TryKillOwnedProcess(ownedWorkerProcess);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出或句柄已经释放。
            }
            finally
            {
                ownedWorkerProcess.Dispose();
                Debug.WriteLine(
                    "母排设置窗口已释放算法 Worker 会话代次：" +
                    ownedWorkerGeneration);
            }
        }

        private static void TryKillOwnedProcess(Process ownedWorkerProcess)
        {
            try
            {
                if (ownedWorkerProcess != null && !ownedWorkerProcess.HasExited)
                {
                    ownedWorkerProcess.Kill();
                    ownedWorkerProcess.WaitForExit(1000);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已经退出。
            }
            catch (Win32Exception)
            {
                // 进程退出与 Kill 竞争时无需再次处理。
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(BusbarAlgorithmWorkerClient));
            }
        }

        private sealed class WorkerSearchSession
        {
            public WorkerSearchSession(
                string requestId,
                int workerGeneration,
                IProgress<BusbarAlgorithmWorkerUpdate> progress)
            {
                RequestId = requestId;
                WorkerGeneration = workerGeneration;
                Progress = progress;
                CompletionSource =
                    new TaskCompletionSource<LinkageRouteWorkerComparisonResultDto>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                CancelAcknowledgedSource = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string RequestId { get; }
            public int WorkerGeneration { get; }
            public IProgress<BusbarAlgorithmWorkerUpdate> Progress { get; }
            public TaskCompletionSource<LinkageRouteWorkerComparisonResultDto>
                CompletionSource { get; }
            public TaskCompletionSource<bool> CancelAcknowledgedSource { get; }
        }
    }

    internal static class BusbarAlgorithmWorkerLocator
    {
        private const string WorkerExecutableName = "RouteLinkBar.Algorithm.Worker.exe";
        private const string WorkerPathEnvironmentVariableName =
            "ROUTELINKBAR_ALGORITHM_WORKER_PATH";

        public static string ResolveWorkerExecutablePath()
        {
            var configuredWorkerPath = Environment.GetEnvironmentVariable(
                WorkerPathEnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(configuredWorkerPath))
            {
                return Path.GetFullPath(configuredWorkerPath);
            }

            return Path.Combine(
                ResolveHostAssemblyDirectory(),
                "Worker", "Algorithm",
                WorkerExecutableName);
        }

        public static string BuildWorkerPackageDisplayText()
        {
            var workerExecutablePath = ResolveWorkerExecutablePath();
            if (!File.Exists(workerExecutablePath))
            {
                return "未检测到 Worker（协议 v" +
                    LinkageRouteWorkerProtocol.CurrentVersion + "）";
            }

            try
            {
                var workerVersionInformation =
                    FileVersionInfo.GetVersionInfo(workerExecutablePath);
                var workerVersionText = string.IsNullOrWhiteSpace(
                    workerVersionInformation.FileVersion)
                    ? "未标注文件版本"
                    : "文件版本 " + workerVersionInformation.FileVersion;
                return "Worker 已就绪 · 协议 v" +
                    LinkageRouteWorkerProtocol.CurrentVersion + " · " +
                    workerVersionText;
            }
            catch (Exception workerVersionException)
            {
                Debug.WriteLine(
                    "读取算法 Worker 文件版本失败：" +
                    workerVersionException.Message);
                return "Worker 已就绪 · 协议 v" +
                    LinkageRouteWorkerProtocol.CurrentVersion;
            }
        }

        private static string ResolveHostAssemblyDirectory()
        {
            var hostAssembly = typeof(BusbarAlgorithmWorkerClient).Assembly;
            try
            {
                var hostAssemblyCodeBase = hostAssembly.CodeBase;
                if (!string.IsNullOrWhiteSpace(hostAssemblyCodeBase))
                {
                    var hostAssemblyUri = new UriBuilder(hostAssemblyCodeBase);
                    var hostAssemblyPath = Uri.UnescapeDataString(hostAssemblyUri.Path);
                    var hostAssemblyDirectory = Path.GetDirectoryName(hostAssemblyPath);
                    if (!string.IsNullOrWhiteSpace(hostAssemblyDirectory))
                    {
                        return hostAssemblyDirectory;
                    }
                }
            }
            catch (Exception codeBaseException)
            {
                Debug.WriteLine(
                    "无法通过 CKBusbarBridge.dll CodeBase 解析安装目录：" +
                    codeBaseException.Message);
            }

            var hostAssemblyLocation = hostAssembly.Location;
            var locationDirectory = string.IsNullOrWhiteSpace(hostAssemblyLocation)
                ? null
                : Path.GetDirectoryName(hostAssemblyLocation);
            if (!string.IsNullOrWhiteSpace(locationDirectory))
            {
                return locationDirectory;
            }

            throw new BusbarAlgorithmWorkerUnavailableException(
                "无法取得 CKBusbarBridge.dll 的实际加载目录，不能定位独立算法程序。");
        }
    }

    internal sealed class BusbarAlgorithmWorkerUpdate
    {
        public BusbarAlgorithmWorkerUpdate(
            LinkageRouteWorkerProgressDto progress,
            LinkageRouteWorkerSolutionDto publishedSolution)
        {
            Progress = progress;
            PublishedSolution = publishedSolution;
        }

        public LinkageRouteWorkerProgressDto Progress { get; }
        public LinkageRouteWorkerSolutionDto PublishedSolution { get; }
    }

    internal sealed class BusbarAlgorithmWorkerUnavailableException : Exception
    {
        public BusbarAlgorithmWorkerUnavailableException(string message)
            : base(message)
        {
        }

        public BusbarAlgorithmWorkerUnavailableException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class BusbarAlgorithmWorkerSearchException : Exception
    {
        public BusbarAlgorithmWorkerSearchException(string message)
            : base(message)
        {
        }
    }

    internal sealed class BusbarAlgorithmWorkerAuthorizationException : Exception
    {
        public BusbarAlgorithmWorkerAuthorizationException(string message)
            : base(message)
        {
        }
    }
}
