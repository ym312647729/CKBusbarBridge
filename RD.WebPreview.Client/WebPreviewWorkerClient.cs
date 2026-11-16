using RD.WebPreview.Contracts;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RD.WebPreview.Client
{
    /// <summary>
    /// 通用 Web 预览 Worker 客户端。一个宿主持有一个实例，只管理自己启动的 PID。
    /// </summary>
    public sealed class WebPreviewWorkerClient : IDisposable
    {
        private const int HandshakeTimeoutMilliseconds = 5000;
        private const int CancellationTimeoutMilliseconds = 2000;
        private const int ShutdownTimeoutMilliseconds = 1500;
        private const string WorkerExecutableName = "RD.WebPreview.Worker.exe";

        private static readonly byte[] WebPreviewAuthorizationEncodingMask =
        {
            0x20, 0xBA, 0xFA, 0x69, 0x85, 0x3D, 0x6F, 0xA4,
            0x91, 0x7D, 0x30, 0xE9, 0x3F, 0x2B, 0xEE, 0x2D
        };

        private static readonly byte[] EncodedWebPreviewAuthorizationCode =
        {
            0x14, 0xFB, 0x80, 0x44, 0xCC, 0x56, 0x3D, 0xC3,
            0xA4, 0x30, 0x46, 0x9F, 0x49, 0x1B, 0xDE, 0x5C,
            0x48, 0x8D, 0xD7, 0x04, 0xEA, 0x74, 0x26, 0xDE,
            0xFF, 0x19, 0x48, 0x85, 0x51, 0x7B, 0x9A, 0x5F,
            0x17, 0x8C, 0xA5, 0x26, 0xC1, 0x59, 0x36, 0xD7,
            0xF6, 0x0D, 0x53
        };

        private readonly object webPreviewWorkerStateGate = new object();
        private readonly object webPreviewProtocolWriteGate = new object();
        private readonly SemaphoreSlim webPreviewWorkerStartupGate =
            new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim webPreviewGenerationGate =
            new SemaphoreSlim(1, 1);
        private Process webPreviewWorkerProcess;
        private StreamWriter webPreviewCommandWriter;
        private string webPreviewAuthorizationCode;
        private TaskCompletionSource<bool> webPreviewHandshakeCompletionSource;
        private string webPreviewHandshakeRequestId;
        private WebPreviewClientGenerationSession activeWebPreviewGenerationSession;
        private int webPreviewWorkerGeneration;
        private bool isWebPreviewClientDisposed;

        public async Task<WebPreviewGenerateResultDto> GenerateAsync(
            WebPreviewSceneDto previewScene,
            IProgress<WebPreviewGenerateProgressDto> previewProgress,
            CancellationToken previewCancellationToken)
        {
            if (previewScene == null)
            {
                throw new ArgumentNullException(nameof(previewScene));
            }

            ThrowIfDisposed();
            await webPreviewGenerationGate.WaitAsync(previewCancellationToken)
                .ConfigureAwait(false);
            WebPreviewClientGenerationSession previewGenerationSession = null;
            try
            {
                ThrowIfDisposed();
                await EnsureWorkerStartedAsync(previewCancellationToken)
                    .ConfigureAwait(false);
                previewCancellationToken.ThrowIfCancellationRequested();

                string currentPreviewAuthorizationCode;
                int currentPreviewWorkerGeneration;
                lock (webPreviewWorkerStateGate)
                {
                    currentPreviewAuthorizationCode = webPreviewAuthorizationCode;
                    currentPreviewWorkerGeneration = webPreviewWorkerGeneration;
                    previewGenerationSession = new WebPreviewClientGenerationSession(
                        Guid.NewGuid().ToString("N"),
                        currentPreviewWorkerGeneration,
                        previewProgress);
                    activeWebPreviewGenerationSession = previewGenerationSession;
                }

                SendMessage(new WebPreviewWorkerMessageDto
                {
                    MessageType = WebPreviewWorkerProtocol.GenerateRequest,
                    RequestId = previewGenerationSession.RequestId,
                    AuthorizationCode = currentPreviewAuthorizationCode,
                    GenerateRequest = new WebPreviewGenerateRequestDto
                    {
                        PayloadMode = WebPreviewWorkerProtocol.InlinePayload,
                        Scene = previewScene,
                        OutputMode = WebPreviewWorkerProtocol.SelfContainedHtml,
                        ExpirationHours = 24
                    }
                }, currentPreviewWorkerGeneration);
                using (previewCancellationToken.Register(() =>
                    RequestGenerationCancellation(previewGenerationSession)))
                {
                    return await previewGenerationSession.CompletionSource.Task
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (previewGenerationSession != null)
                {
                    lock (webPreviewWorkerStateGate)
                    {
                        if (ReferenceEquals(
                            activeWebPreviewGenerationSession,
                            previewGenerationSession))
                        {
                            activeWebPreviewGenerationSession = null;
                        }
                    }
                }

                webPreviewGenerationGate.Release();
            }
        }

        public async Task CancelActiveGenerationAsync()
        {
            WebPreviewClientGenerationSession previewGenerationSession;
            lock (webPreviewWorkerStateGate)
            {
                previewGenerationSession = activeWebPreviewGenerationSession;
            }

            if (previewGenerationSession == null)
            {
                return;
            }

            SendCancellation(previewGenerationSession);
            var previewCancellationTimeoutTask = Task.Delay(
                CancellationTimeoutMilliseconds);
            var previewCompletedTask = await Task.WhenAny(
                    previewGenerationSession.CompletionSource.Task,
                    previewCancellationTimeoutTask)
                .ConfigureAwait(false);
            if (ReferenceEquals(
                previewCompletedTask,
                previewCancellationTimeoutTask))
            {
                previewGenerationSession.CompletionSource.TrySetCanceled();
                StopWorkerImmediately(previewGenerationSession.WorkerGeneration);
            }
        }

        public void Dispose()
        {
            Process ownedWebPreviewWorkerProcess;
            StreamWriter ownedWebPreviewCommandWriter;
            WebPreviewClientGenerationSession previewGenerationSession;
            TaskCompletionSource<bool> previewHandshakeCompletionSource;
            lock (webPreviewWorkerStateGate)
            {
                if (isWebPreviewClientDisposed)
                {
                    return;
                }

                isWebPreviewClientDisposed = true;
                ownedWebPreviewWorkerProcess = webPreviewWorkerProcess;
                ownedWebPreviewCommandWriter = webPreviewCommandWriter;
                previewGenerationSession = activeWebPreviewGenerationSession;
                previewHandshakeCompletionSource =
                    webPreviewHandshakeCompletionSource;
                webPreviewWorkerProcess = null;
                webPreviewCommandWriter = null;
                webPreviewAuthorizationCode = null;
                activeWebPreviewGenerationSession = null;
                webPreviewHandshakeCompletionSource = null;
                webPreviewHandshakeRequestId = null;
            }

            previewGenerationSession?.CompletionSource.TrySetCanceled();
            previewHandshakeCompletionSource?.TrySetCanceled();
            try
            {
                if (ownedWebPreviewWorkerProcess != null &&
                    !ownedWebPreviewWorkerProcess.HasExited &&
                    ownedWebPreviewCommandWriter != null)
                {
                    ownedWebPreviewCommandWriter.WriteLine(
                        WebPreviewWorkerMessageCodec.SerializeMessage(
                            new WebPreviewWorkerMessageDto
                            {
                                MessageType = WebPreviewWorkerProtocol.Shutdown,
                                RequestId = Guid.NewGuid().ToString("N")
                            }));
                    ownedWebPreviewCommandWriter.Flush();
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                ownedWebPreviewCommandWriter?.Close();
            }
            catch (IOException)
            {
            }

            if (ownedWebPreviewWorkerProcess != null)
            {
                try
                {
                    if (!ownedWebPreviewWorkerProcess.HasExited &&
                        !ownedWebPreviewWorkerProcess.WaitForExit(
                            ShutdownTimeoutMilliseconds))
                    {
                        TryKillProcess(ownedWebPreviewWorkerProcess);
                    }
                }
                finally
                {
                    ownedWebPreviewWorkerProcess.Dispose();
                }
            }

            webPreviewWorkerStartupGate.Dispose();
            webPreviewGenerationGate.Dispose();
        }

        private async Task EnsureWorkerStartedAsync(
            CancellationToken previewCancellationToken)
        {
            if (IsWorkerAvailable())
            {
                return;
            }

            await webPreviewWorkerStartupGate.WaitAsync(previewCancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (IsWorkerAvailable())
                {
                    return;
                }

                StopWorkerImmediately();
                StartWorkerProcess();
                Task previewHandshakeTask;
                lock (webPreviewWorkerStateGate)
                {
                    previewHandshakeTask =
                        webPreviewHandshakeCompletionSource.Task;
                }

                var previewHandshakeTimeoutTask = Task.Delay(
                    HandshakeTimeoutMilliseconds,
                    previewCancellationToken);
                var completedPreviewHandshakeTask = await Task.WhenAny(
                        previewHandshakeTask,
                        previewHandshakeTimeoutTask)
                    .ConfigureAwait(false);
                if (!ReferenceEquals(
                    completedPreviewHandshakeTask,
                    previewHandshakeTask))
                {
                    previewCancellationToken.ThrowIfCancellationRequested();
                    StopWorkerImmediately();
                    throw new WebPreviewWorkerUnavailableException(
                        "Web 预览 Worker 握手超时。" );
                }

                await previewHandshakeTask.ConfigureAwait(false);
            }
            finally
            {
                webPreviewWorkerStartupGate.Release();
            }
        }

        private bool IsWorkerAvailable()
        {
            lock (webPreviewWorkerStateGate)
            {
                return !isWebPreviewClientDisposed &&
                    webPreviewWorkerProcess != null &&
                    !webPreviewWorkerProcess.HasExited &&
                    webPreviewCommandWriter != null &&
                    webPreviewHandshakeCompletionSource != null &&
                    webPreviewHandshakeCompletionSource.Task.Status ==
                        TaskStatus.RanToCompletion;
            }
        }

        private void StartWorkerProcess()
        {
            var webPreviewWorkerExecutablePath = ResolveWorkerExecutablePath();
            if (!File.Exists(webPreviewWorkerExecutablePath))
            {
                throw new WebPreviewWorkerUnavailableException(
                    "未找到 Web 预览 Worker：" +
                    webPreviewWorkerExecutablePath +
                    "。请在 Visual Studio 中重新生成解决方案后重试。" );
            }

            var resolvedWebPreviewAuthorizationCode =
                ResolveWorkerAuthorizationCode();
            var newWebPreviewWorkerGeneration = Interlocked.Increment(
                ref webPreviewWorkerGeneration);
            var previewHandshakeRequestId = Guid.NewGuid().ToString("N");
            var webPreviewWorkerStartInfo = new ProcessStartInfo
            {
                FileName = webPreviewWorkerExecutablePath,
                Arguments = "--parent-pid " + GetCurrentProcessId(),
                WorkingDirectory = Path.GetDirectoryName(
                    webPreviewWorkerExecutablePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            var startedWebPreviewWorkerProcess = new Process
            {
                StartInfo = webPreviewWorkerStartInfo,
                EnableRaisingEvents = true
            };
            startedWebPreviewWorkerProcess.Exited +=
                (eventSender, eventArguments) => HandleWorkerUnavailable(
                    startedWebPreviewWorkerProcess,
                    newWebPreviewWorkerGeneration,
                    "Web 预览 Worker 已退出。",
                    null);
            startedWebPreviewWorkerProcess.ErrorDataReceived +=
                (eventSender, eventArguments) =>
                {
                    if (!string.IsNullOrWhiteSpace(eventArguments.Data))
                    {
                        Debug.WriteLine(
                            "[WebPreviewWorker] " + eventArguments.Data);
                    }
                };

            try
            {
                if (!startedWebPreviewWorkerProcess.Start())
                {
                    throw new InvalidOperationException(
                        "Process.Start 返回 false。" );
                }

                var webPreviewWriter = new StreamWriter(
                    startedWebPreviewWorkerProcess.StandardInput.BaseStream,
                    new UTF8Encoding(false),
                    4096,
                    false)
                {
                    AutoFlush = true
                };
                var previewHandshakeCompletionSource =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                lock (webPreviewWorkerStateGate)
                {
                    webPreviewWorkerProcess = startedWebPreviewWorkerProcess;
                    webPreviewCommandWriter = webPreviewWriter;
                    webPreviewAuthorizationCode =
                        resolvedWebPreviewAuthorizationCode;
                    webPreviewHandshakeRequestId = previewHandshakeRequestId;
                    webPreviewHandshakeCompletionSource =
                        previewHandshakeCompletionSource;
                }

                startedWebPreviewWorkerProcess.BeginErrorReadLine();
                Task.Run(() => ReadProtocolLoopAsync(
                    startedWebPreviewWorkerProcess,
                    newWebPreviewWorkerGeneration));
                SendMessage(new WebPreviewWorkerMessageDto
                {
                    MessageType = WebPreviewWorkerProtocol.Handshake,
                    RequestId = previewHandshakeRequestId,
                    ParentProcessId = GetCurrentProcessId(),
                    AuthorizationCode = resolvedWebPreviewAuthorizationCode
                }, newWebPreviewWorkerGeneration);
            }
            catch (Exception previewWorkerStartException)
            {
                TryKillProcess(startedWebPreviewWorkerProcess);
                startedWebPreviewWorkerProcess.Dispose();
                throw new WebPreviewWorkerUnavailableException(
                    "启动 Web 预览 Worker 失败：" +
                    previewWorkerStartException.Message,
                    previewWorkerStartException);
            }
        }

        private async Task ReadProtocolLoopAsync(
            Process protocolWebPreviewWorkerProcess,
            int protocolWebPreviewWorkerGeneration)
        {
            try
            {
                string previewProtocolLine;
                while ((previewProtocolLine = await protocolWebPreviewWorkerProcess
                    .StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    HandleWorkerMessage(
                        WebPreviewWorkerMessageCodec.DeserializeMessage(
                            previewProtocolLine),
                        protocolWebPreviewWorkerGeneration);
                }

                HandleWorkerUnavailable(
                    protocolWebPreviewWorkerProcess,
                    protocolWebPreviewWorkerGeneration,
                    "Web 预览 Worker 通信管道已关闭。",
                    null);
            }
            catch (Exception previewProtocolReadException)
            {
                HandleWorkerUnavailable(
                    protocolWebPreviewWorkerProcess,
                    protocolWebPreviewWorkerGeneration,
                    "读取 Web 预览 Worker 协议失败。",
                    previewProtocolReadException);
            }
        }

        private void HandleWorkerMessage(
            WebPreviewWorkerMessageDto workerMessage,
            int protocolWebPreviewWorkerGeneration)
        {
            if (workerMessage.ProtocolVersion !=
                WebPreviewWorkerProtocol.CurrentVersion)
            {
                HandleWorkerUnavailable(
                    webPreviewWorkerProcess,
                    protocolWebPreviewWorkerGeneration,
                    "Web 预览 Worker 协议版本不一致。",
                    null);
                return;
            }

            TaskCompletionSource<bool> previewHandshakeCompletionSource;
            string previewHandshakeRequestId;
            WebPreviewClientGenerationSession previewGenerationSession;
            lock (webPreviewWorkerStateGate)
            {
                if (protocolWebPreviewWorkerGeneration !=
                    webPreviewWorkerGeneration)
                {
                    return;
                }

                previewHandshakeCompletionSource =
                    webPreviewHandshakeCompletionSource;
                previewHandshakeRequestId =
                    webPreviewHandshakeRequestId;
                previewGenerationSession =
                    activeWebPreviewGenerationSession;
            }

            if (string.Equals(
                    workerMessage.MessageType,
                    WebPreviewWorkerProtocol.HandshakeAcknowledged,
                    StringComparison.Ordinal) &&
                string.Equals(
                    workerMessage.RequestId,
                    previewHandshakeRequestId,
                    StringComparison.Ordinal))
            {
                previewHandshakeCompletionSource?.TrySetResult(true);
                return;
            }

            if (string.Equals(
                    workerMessage.MessageType,
                    WebPreviewWorkerProtocol.Faulted,
                    StringComparison.Ordinal) &&
                string.Equals(
                    workerMessage.RequestId,
                    previewHandshakeRequestId,
                    StringComparison.Ordinal))
            {
                previewHandshakeCompletionSource?.TrySetException(
                    string.Equals(
                        workerMessage.ErrorCode,
                        "AuthorizationFailed",
                        StringComparison.Ordinal)
                        ? (Exception)new WebPreviewWorkerAuthorizationException(
                            workerMessage.ErrorMessage ??
                            "Web 预览 Worker 授权失败。" )
                        : new WebPreviewWorkerUnavailableException(
                            workerMessage.ErrorMessage ??
                            "Web 预览 Worker 握手失败。" ));
                return;
            }

            if (previewGenerationSession == null ||
                previewGenerationSession.WorkerGeneration !=
                    protocolWebPreviewWorkerGeneration ||
                !string.Equals(
                    previewGenerationSession.RequestId,
                    workerMessage.RequestId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                WebPreviewWorkerProtocol.GenerateProgress,
                StringComparison.Ordinal))
            {
                previewGenerationSession.Progress?.Report(workerMessage.Progress);
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                WebPreviewWorkerProtocol.Generated,
                StringComparison.Ordinal))
            {
                previewGenerationSession.CompletionSource.TrySetResult(
                    workerMessage.GenerateResult);
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                WebPreviewWorkerProtocol.Cancelled,
                StringComparison.Ordinal))
            {
                previewGenerationSession.CompletionSource.TrySetCanceled();
                return;
            }

            if (string.Equals(
                workerMessage.MessageType,
                WebPreviewWorkerProtocol.Faulted,
                StringComparison.Ordinal))
            {
                previewGenerationSession.CompletionSource.TrySetException(
                    string.Equals(
                        workerMessage.ErrorCode,
                        "AuthorizationFailed",
                        StringComparison.Ordinal)
                        ? (Exception)new WebPreviewWorkerAuthorizationException(
                            workerMessage.ErrorMessage ??
                            "Web 预览 Worker 授权失败。" )
                        : new WebPreviewGenerationException(
                            workerMessage.ErrorMessage ??
                            "Web 预览文件生成失败。" ));
            }
        }

        private void RequestGenerationCancellation(
            WebPreviewClientGenerationSession previewGenerationSession)
        {
            if (previewGenerationSession == null)
            {
                return;
            }

            SendCancellation(previewGenerationSession);
            Task.Run(async () =>
            {
                var previewCancellationTimeoutTask = Task.Delay(
                    CancellationTimeoutMilliseconds);
                var previewCompletedTask = await Task.WhenAny(
                        previewGenerationSession.CompletionSource.Task,
                        previewCancellationTimeoutTask)
                    .ConfigureAwait(false);
                if (!ReferenceEquals(
                    previewCompletedTask,
                    previewCancellationTimeoutTask))
                {
                    return;
                }

                previewGenerationSession.CompletionSource.TrySetCanceled();
                StopWorkerImmediately(
                    previewGenerationSession.WorkerGeneration);
            });
        }

        private void SendCancellation(
            WebPreviewClientGenerationSession previewGenerationSession)
        {
            try
            {
                SendMessage(new WebPreviewWorkerMessageDto
                {
                    MessageType = WebPreviewWorkerProtocol.CancelRequest,
                    RequestId = previewGenerationSession.RequestId
                }, previewGenerationSession.WorkerGeneration);
            }
            catch (WebPreviewWorkerUnavailableException)
            {
                previewGenerationSession.CompletionSource.TrySetCanceled();
            }
        }

        private void SendMessage(
            WebPreviewWorkerMessageDto workerMessage,
            int requiredWebPreviewWorkerGeneration)
        {
            StreamWriter previewCommandWriter;
            lock (webPreviewWorkerStateGate)
            {
                if (isWebPreviewClientDisposed ||
                    requiredWebPreviewWorkerGeneration !=
                        webPreviewWorkerGeneration)
                {
                    throw new WebPreviewWorkerUnavailableException(
                        "Web 预览 Worker 会话已失效。" );
                }

                previewCommandWriter = webPreviewCommandWriter;
            }

            if (previewCommandWriter == null)
            {
                throw new WebPreviewWorkerUnavailableException(
                    "Web 预览 Worker 通信管道不可用。" );
            }

            try
            {
                var previewMessageJson =
                    WebPreviewWorkerMessageCodec.SerializeMessage(workerMessage);
                lock (webPreviewProtocolWriteGate)
                {
                    previewCommandWriter.WriteLine(previewMessageJson);
                    previewCommandWriter.Flush();
                }
            }
            catch (Exception previewProtocolWriteException)
                when (previewProtocolWriteException is IOException ||
                      previewProtocolWriteException is ObjectDisposedException ||
                      previewProtocolWriteException is InvalidOperationException)
            {
                throw new WebPreviewWorkerUnavailableException(
                    "写入 Web 预览 Worker 协议失败。",
                    previewProtocolWriteException);
            }
        }

        private void HandleWorkerUnavailable(
            Process unavailableWorkerProcess,
            int unavailableWorkerGeneration,
            string unavailableMessage,
            Exception unavailableException)
        {
            WebPreviewClientGenerationSession previewGenerationSession;
            TaskCompletionSource<bool> previewHandshakeCompletionSource;
            lock (webPreviewWorkerStateGate)
            {
                if (unavailableWorkerGeneration != webPreviewWorkerGeneration ||
                    !ReferenceEquals(
                        webPreviewWorkerProcess,
                        unavailableWorkerProcess))
                {
                    return;
                }

                previewGenerationSession = activeWebPreviewGenerationSession;
                previewHandshakeCompletionSource =
                    webPreviewHandshakeCompletionSource;
                webPreviewWorkerProcess = null;
                webPreviewCommandWriter = null;
                webPreviewAuthorizationCode = null;
                activeWebPreviewGenerationSession = null;
                webPreviewHandshakeCompletionSource = null;
                webPreviewHandshakeRequestId = null;
            }

            var workerUnavailableException =
                new WebPreviewWorkerUnavailableException(
                    unavailableMessage,
                    unavailableException);
            previewHandshakeCompletionSource?.TrySetException(
                workerUnavailableException);
            previewGenerationSession?.CompletionSource.TrySetException(
                workerUnavailableException);
            try
            {
                unavailableWorkerProcess?.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void StopWorkerImmediately(
            int? requiredWebPreviewWorkerGeneration = null)
        {
            Process ownedWebPreviewWorkerProcess;
            StreamWriter ownedWebPreviewCommandWriter;
            WebPreviewClientGenerationSession previewGenerationSession;
            TaskCompletionSource<bool> previewHandshakeCompletionSource;
            lock (webPreviewWorkerStateGate)
            {
                if (requiredWebPreviewWorkerGeneration.HasValue &&
                    requiredWebPreviewWorkerGeneration.Value !=
                        webPreviewWorkerGeneration)
                {
                    return;
                }

                ownedWebPreviewWorkerProcess = webPreviewWorkerProcess;
                ownedWebPreviewCommandWriter = webPreviewCommandWriter;
                previewGenerationSession = activeWebPreviewGenerationSession;
                previewHandshakeCompletionSource =
                    webPreviewHandshakeCompletionSource;
                webPreviewWorkerProcess = null;
                webPreviewCommandWriter = null;
                webPreviewAuthorizationCode = null;
                activeWebPreviewGenerationSession = null;
                webPreviewHandshakeCompletionSource = null;
                webPreviewHandshakeRequestId = null;
            }

            previewGenerationSession?.CompletionSource.TrySetException(
                new WebPreviewWorkerUnavailableException(
                    "Web 预览 Worker 已被回收。" ));
            previewHandshakeCompletionSource?.TrySetException(
                new WebPreviewWorkerUnavailableException(
                    "Web 预览 Worker 已被回收。" ));
            try
            {
                ownedWebPreviewCommandWriter?.Close();
            }
            catch (IOException)
            {
            }

            if (ownedWebPreviewWorkerProcess != null)
            {
                TryKillProcess(ownedWebPreviewWorkerProcess);
                ownedWebPreviewWorkerProcess.Dispose();
            }
        }

        private static string ResolveWorkerExecutablePath()
        {
            var configuredWebPreviewWorkerPath = Environment.GetEnvironmentVariable(
                "RD_WEB_PREVIEW_WORKER_PATH");
            if (!string.IsNullOrWhiteSpace(configuredWebPreviewWorkerPath))
            {
                return Path.GetFullPath(configuredWebPreviewWorkerPath);
            }

            var clientAssemblyDirectory = ResolveClientAssemblyDirectory();
            var isolatedWebPreviewWorkerPath = Path.Combine(
                clientAssemblyDirectory,
                "WebPreviewWorker",
                WorkerExecutableName);
            if (File.Exists(isolatedWebPreviewWorkerPath))
            {
                return isolatedWebPreviewWorkerPath;
            }

            var colocatedWebPreviewWorkerPath = Path.Combine(
                clientAssemblyDirectory,
                WorkerExecutableName);
            return File.Exists(colocatedWebPreviewWorkerPath)
                ? colocatedWebPreviewWorkerPath
                : isolatedWebPreviewWorkerPath;
        }

        private static string ResolveClientAssemblyDirectory()
        {
            var clientAssembly = typeof(WebPreviewWorkerClient).Assembly;
            try
            {
                var clientAssemblyCodeBase = clientAssembly.CodeBase;
                if (!string.IsNullOrWhiteSpace(clientAssemblyCodeBase))
                {
                    var clientAssemblyUri = new UriBuilder(
                        clientAssemblyCodeBase).Uri;
                    var clientAssemblyPath = Uri.UnescapeDataString(
                        clientAssemblyUri.LocalPath);
                    return Path.GetDirectoryName(clientAssemblyPath) ??
                        AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            catch (UriFormatException)
            {
            }

            return Path.GetDirectoryName(clientAssembly.Location) ??
                AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string ResolveWorkerAuthorizationCode()
        {
            var decodedAuthorizationCharacters =
                new char[EncodedWebPreviewAuthorizationCode.Length];
            for (var authorizationCharacterIndex = 0;
                authorizationCharacterIndex <
                    EncodedWebPreviewAuthorizationCode.Length;
                authorizationCharacterIndex++)
            {
                var authorizationMaskIndex = authorizationCharacterIndex %
                    WebPreviewAuthorizationEncodingMask.Length;
                decodedAuthorizationCharacters[authorizationCharacterIndex] =
                    (char)(EncodedWebPreviewAuthorizationCode[
                        authorizationCharacterIndex] ^
                    WebPreviewAuthorizationEncodingMask[
                        authorizationMaskIndex]);
            }

            try
            {
                return new string(decodedAuthorizationCharacters);
            }
            finally
            {
                Array.Clear(
                    decodedAuthorizationCharacters,
                    0,
                    decodedAuthorizationCharacters.Length);
            }
        }

        private static int GetCurrentProcessId()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                return currentProcess.Id;
            }
        }

        private static void TryKillProcess(Process ownedWorkerProcess)
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
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (isWebPreviewClientDisposed)
            {
                throw new ObjectDisposedException(nameof(WebPreviewWorkerClient));
            }
        }

        private sealed class WebPreviewClientGenerationSession
        {
            public WebPreviewClientGenerationSession(
                string requestId,
                int workerGeneration,
                IProgress<WebPreviewGenerateProgressDto> progress)
            {
                RequestId = requestId;
                WorkerGeneration = workerGeneration;
                Progress = progress;
                CompletionSource =
                    new TaskCompletionSource<WebPreviewGenerateResultDto>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string RequestId { get; }

            public int WorkerGeneration { get; }

            public IProgress<WebPreviewGenerateProgressDto> Progress { get; }

            public TaskCompletionSource<WebPreviewGenerateResultDto>
                CompletionSource { get; }
        }
    }

    public sealed class WebPreviewWorkerUnavailableException : Exception
    {
        public WebPreviewWorkerUnavailableException(string message)
            : base(message)
        {
        }

        public WebPreviewWorkerUnavailableException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class WebPreviewWorkerAuthorizationException : Exception
    {
        public WebPreviewWorkerAuthorizationException(string message)
            : base(message)
        {
        }
    }

    public sealed class WebPreviewGenerationException : Exception
    {
        public WebPreviewGenerationException(string message)
            : base(message)
        {
        }
    }
}
