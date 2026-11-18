using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Computation
{
    /// <summary>Persistent snapshot calculator. No SolidWorks objects, windows or UI dispatcher.</summary>
    public sealed class PreviewCalculator : IDisposable
    {
        private readonly string _workerDirectory;
        private readonly Func<string, JObject, Task<JToken>> _managedCalculation;
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        private Process _process;
        private StreamWriter _commandWriter;
        private TaskCompletionSource<bool> _ready;
        private bool _disposed;
        private string _lastDiagnostic;

        public PreviewCalculator(string workerDirectory, Func<string, JObject, Task<JToken>> managedCalculation = null)
        {
            _workerDirectory = Path.GetFullPath(workerDirectory ?? throw new ArgumentNullException(nameof(workerDirectory)));
            _managedCalculation = managedCalculation;
        }

        public async Task<JObject> ComputeAsync(JObject request, CancellationToken cancellationToken = default)
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PreviewCalculator));
                if (_process == null || _process.HasExited) await StartAsync().ConfigureAwait(false);
                var identifier = Guid.NewGuid().ToString("N");
                var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                var snapshot = (JObject)request.DeepClone(); snapshot["requestId"] = identifier;
                snapshot["maximumWorkers"] = Math.Min(CalculationConcurrency.PreviewWorkers,
                    Math.Max(1, (int?)request["maximumWorkers"] ?? CalculationConcurrency.MaximumWorkers));
                _pending[identifier] = completion;
                try
                {
                    await WriteAsync(snapshot).ConfigureAwait(false);
                    using (cancellationToken.Register(() => completion.TrySetCanceled()))
                    {
                        if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false) != completion.Task)
                            throw new TimeoutException("预览计算超时，当前参数尚未完成。");
                        return await completion.Task.ConfigureAwait(false);
                    }
                }
                catch { StopProcess(); throw; }
                finally { _pending.TryRemove(identifier, out _); }
            }
            catch { StopProcess(); throw; }
            finally { _requestGate.Release(); }
        }

        private async Task StartAsync()
        {
            var engine = Path.Combine(_workerDirectory, "Runtime", "node.exe");
            var host = Path.Combine(_workerDirectory, "PreviewRuntime", "node-host.mjs");
            if (!File.Exists(engine) || !File.Exists(host)) throw new FileNotFoundException("Worker 运行文件不完整，请重新生成 Debug。");
            var ready = _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = new ProcessStartInfo(engine,
                "\"" + host + "\" --compact-meshes --parent " + Process.GetCurrentProcess().Id)
            {
                WorkingDirectory = _workerDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            }, EnableRaisingEvents = true };
            process.Exited += (sender, args) =>
            {
                if (!ReferenceEquals(_process, process)) return;
                Fail(new IOException("预览 Worker 已退出。" + _lastDiagnostic));
            };
            _process = process;
            process.Start();
            _commandWriter = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            _ = Task.Run(async () =>
            {
                try
                {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        var parseWatch = Stopwatch.StartNew();
                        var response = JObject.Parse(line);
                        var parseMilliseconds = parseWatch.Elapsed.TotalMilliseconds;
                        MeshTransport.Expand(response);
                        if (response["timings"] is JObject responseTimings)
                        {
                            responseTimings["clientParseMs"] = parseMilliseconds;
                            responseTimings["clientDecodeMs"] = parseWatch.Elapsed.TotalMilliseconds - parseMilliseconds;
                        }
                        if ((string)response["type"] == "ready") { ready.TrySetResult(true); continue; }
                        if ((string)response["type"] == "managed-request") { _ = ReplyManagedAsync(response); continue; }
                        if ((string)response["type"] == "fatal") throw new InvalidOperationException((string)response["error"]);
                        if (_pending.TryGetValue((string)response["requestId"] ?? "", out var pending))
                        {
                            if ((bool?)response["ok"] == true) pending.TrySetResult(response);
                            else pending.TrySetException(new InvalidOperationException((string)response["error"]));
                        }
                    }
                }
                catch (Exception error) { if (ReferenceEquals(_process, process)) Fail(error); }
            });
            _ = Task.Run(async () =>
            {
                try { string line; while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) _lastDiagnostic = line; }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
            });
            if (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != ready.Task)
                throw new TimeoutException("预览 Worker 启动超时。" + _lastDiagnostic);
            await ready.Task.ConfigureAwait(false);
        }

        private async Task ReplyManagedAsync(JObject message)
        {
            var reply = new JObject { ["type"] = "managed-response", ["managedId"] = message["managedId"] };
            try
            {
                var method = (string)message["method"];
                if (method == "wall-conductors")
                    reply["result"] = await Task.Run(() => WallConductorCalculator.Read((JObject)message["input"])).ConfigureAwait(false);
                else if (_managedCalculation != null)
                    reply["result"] = await _managedCalculation(method, (JObject)message["input"]).ConfigureAwait(false);
                else throw new InvalidOperationException("不支持的计算请求：" + method);
                reply["ok"] = true;
            }
            catch (Exception error) { reply["ok"] = false; reply["error"] = error.Message; }
            try { await WriteAsync(reply).ConfigureAwait(false); }
            catch (Exception error) { Fail(error); }
        }

        private async Task WriteAsync(JObject value)
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var process = _process;
                if (process == null || process.HasExited) throw new IOException("预览 Worker 未运行。");
                await _commandWriter.WriteLineAsync(value.ToString(Formatting.None)).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }
        private void Fail(Exception error)
        {
            _ready?.TrySetException(error);
            foreach (var completion in _pending.Values) completion.TrySetException(error);
        }
        private void StopProcess()
        {
            var owned = Interlocked.Exchange(ref _process, null);
            if (owned == null) return;
            var writer = Interlocked.Exchange(ref _commandWriter, null);
            try
            {
                try { writer?.Dispose(); owned.StandardInput.Close(); }
                catch (InvalidOperationException) { }
                catch (IOException) { }
                if (!owned.HasExited)
                {
                    try { owned.Kill(); }
                    // EOF 已触发正常退出时，Windows 可能在终止窗口返回拒绝访问；仅确认已退出后接受。
                    catch (Win32Exception error) when (error.NativeErrorCode == 5 && owned.WaitForExit(100)) { }
                }
            }
            catch (InvalidOperationException) { }
            finally { owned.Dispose(); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Fail(new ObjectDisposedException(nameof(PreviewCalculator))); StopProcess();
        }
    }
}
