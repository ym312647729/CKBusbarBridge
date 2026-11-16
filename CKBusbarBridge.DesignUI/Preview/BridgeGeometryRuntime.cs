using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // A document-owned persistent computation host. It has no canvas and exposes no native objects to JS.
    internal sealed class BridgeGeometryRuntime : IDisposable
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _pending = new Dictionary<string, TaskCompletionSource<JObject>>();
        private HwndSource _source;
        private WebView2 _browser;
        private Task _initialization;
        private TaskCompletionSource<bool> _ready;
        private bool _disposed;
        private bool _broken;
        private readonly Dictionary<string, BridgeMeshShape> _shapes = new Dictionary<string, BridgeMeshShape>();
        private readonly LinkedList<string> _shapeUsage = new LinkedList<string>();
        private const long MaximumShapeBytes = 192L * 1024 * 1024;
        private long _shapeBytes;
        private static string RuntimeDirectory => Path.Combine(Path.GetDirectoryName(typeof(BridgeGeometryRuntime).Assembly.Location), "PreviewRuntime");
        internal static string PackageVersion
        {
            get { var manifest = JObject.Parse(File.ReadAllText(Path.Combine(RuntimeDirectory, "runtime-manifest.json")));
                return (string)manifest["calculatorVersion"] + ":" + (string)manifest["packageSha256"]; }
        }

        internal async Task<JObject> Compute(JObject request)
        {
            _dispatcher.VerifyAccess();
            if (_disposed) throw new ObjectDisposedException(nameof(BridgeGeometryRuntime));
            if (_broken) { ResetBrowser(); _initialization = null; _broken = false; }
            var initialization = _initialization ?? (_initialization = Initialize());
            var browser = _browser;
            try { await initialization.ConfigureAwait(false); }
            catch
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentBrowser(browser) || !ReferenceEquals(initialization, _initialization)) return;
                    ResetBrowser(); _initialization = null;
                }).Task.ConfigureAwait(false);
                throw;
            }
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    EnsureCurrentBrowser(browser);
                    request["knownShapes"] = JObject.FromObject(_shapes.ToDictionary(pair => pair.Key, pair => pair.Value.Signature));
                    _pending.Add(id, completion); request["requestId"] = id;
                    browser.CoreWebView2.PostWebMessageAsJson(request.ToString(Formatting.None));
                }).Task.ConfigureAwait(false);
                if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false) != completion.Task)
                {
                    var timeout = new TimeoutException("离线预览计算超时，当前修订未就绪。请修正参数或重试。");
                    await _dispatcher.InvokeAsync(() => { if (IsCurrentBrowser(browser)) Fail(timeout); }).Task.ConfigureAwait(false);
                    throw timeout;
                }
                var response = await completion.Task.ConfigureAwait(false);
                if ((string)response["calculatorVersion"] != PackageVersion) throw new InvalidOperationException("离线计算结果的运行包版本不匹配。");
                return response;
            }
            finally { await _dispatcher.InvokeAsync(() => { _pending.Remove(id); }).Task.ConfigureAwait(false); }
        }

        internal BridgeMeshFrame CreateFrame(Guid id, long revision, JObject response)
        {
            var available = new Dictionary<string, BridgeMeshShape>(_shapes);
            Func<JObject, BridgeMeshInstance> parse = value =>
            {
                var key = (string)value["geometryKey"]; var signature = (string)value["geometrySignature"];
                if (!available.TryGetValue(key, out var shape) || shape.Signature != signature)
                {
                    if (value["positions"] == null) throw new InvalidOperationException("计算器引用了已释放或签名不匹配的网格。");
                    var created = new BridgeMeshShape(value);
                    if (_shapes.TryGetValue(key, out var replaced)) { _shapeBytes -= replaced.Bytes; _shapes.Remove(key); _shapeUsage.Remove(key); }
                    while (_shapeBytes + created.Bytes > MaximumShapeBytes && _shapeUsage.First != null)
                    { var expired = _shapeUsage.First.Value; _shapeUsage.RemoveFirst(); _shapeBytes -= _shapes[expired].Bytes; _shapes.Remove(expired); }
                    if (created.Bytes <= MaximumShapeBytes) { _shapes.Add(key, created); _shapeBytes += created.Bytes; }
                    shape = created;
                }
                available[key] = shape;
                _shapeUsage.Remove(key); if (_shapes.ContainsKey(key)) _shapeUsage.AddLast(key);
                return new BridgeMeshInstance(value, shape);
            };
            var instances = response["instances"].OfType<JObject>().Select(parse).ToArray();
            var interaction = response["interactionInstances"]?.OfType<JObject>().Select(parse).ToArray();
            return new BridgeMeshFrame(id, revision, instances, (string)response["warning"], interaction);
        }

        private async Task Initialize()
        {
            var root = RuntimeDirectory;
            if (!File.Exists(Path.Combine(root, "index.html"))) throw new FileNotFoundException("离线预览运行包缺失：" + root);
            _source = new HwndSource(new HwndSourceParameters("DQBridge geometry computation")
            { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
            var browser = _browser = new WebView2 { Width = 1, Height = 1 }; _source.RootVisual = browser;
            var ready = _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var profile = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "CKBusbarBridge", "PreviewRuntime", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentBrowser(browser);
                return browser.EnsureCoreWebView2Async(environment);
            }).Task.Unwrap().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentBrowser(browser);
                var core = browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.SetVirtualHostNameToFolderMapping("dq-preview.local", root, CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += Receive;
                core.NavigationCompleted += (sender, args) =>
                { if (!args.IsSuccess) ready.TrySetException(new IOException("离线计算页面载入失败：" + args.WebErrorStatus)); };
                core.ProcessFailed += (sender, args) =>
                { if (IsCurrentBrowser(browser)) Fail(new InvalidOperationException("离线预览计算进程退出：" + args.ProcessFailedKind)); };
                core.Navigate("https://dq-preview.local/index.html");
            }).Task.ConfigureAwait(false);
            if (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != ready.Task)
                throw new TimeoutException("离线预览计算器未就绪，请检查WebView2运行环境。");
            await ready.Task.ConfigureAwait(false);
        }

        private void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_disposed) return;
            try
            {
                var data = JObject.Parse(args.WebMessageAsJson);
                if ((string)data["type"] == "ready") { _ready?.TrySetResult(true); return; }
                if ((string)data["type"] == "managed-request") { ReplyManaged(data); return; }
                if ((string)data["type"] == "fatal") { Fail(new InvalidOperationException((string)data["error"])); return; }
                if (!_pending.TryGetValue((string)data["requestId"] ?? "", out var completion)) return;
                if ((bool?)data["ok"] != true) completion.TrySetException(new InvalidOperationException((string)data["error"]));
                else completion.TrySetResult(data);
            }
            catch (Exception error) { Fail(error); }
        }
        private async void ReplyManaged(JObject request)
        {
            var browser = _browser;
            var response = new JObject { ["type"] = "managed-response", ["managedId"] = request["managedId"] };
            try
            {
                if ((string)request["method"] != "wall-conductors") throw new InvalidOperationException("离线预览请求了不支持的业务识别入口。");
                response["result"] = await Task.Run(() => BridgeWallConductorSnapshotReader.Read((JObject)request["input"])).ConfigureAwait(false); response["ok"] = true;
            }
            catch (Exception error) { response["ok"] = false; response["error"] = error.Message; }
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentBrowser(browser)) return;
                    try { browser.CoreWebView2.PostWebMessageAsJson(response.ToString(Formatting.None)); }
                    catch (Exception error) { Fail(error); }
                }).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted)
            {
                // The owning UI is already closing; there is no browser to receive this reply.
            }
        }

        private bool IsCurrentBrowser(WebView2 browser) => !_disposed && ReferenceEquals(browser, _browser);
        private void EnsureCurrentBrowser(WebView2 browser)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BridgeGeometryRuntime));
            if (browser == null || !IsCurrentBrowser(browser)) throw new InvalidOperationException("离线预览计算器已重新初始化，请重试。");
        }

        private void Fail(Exception error)
        {
            _dispatcher.VerifyAccess();
            _broken = true; _ready?.TrySetException(error);
            foreach (var completion in _pending.Values.ToArray()) completion.TrySetException(error);
        }
        private void ResetBrowser()
        {
            _dispatcher.VerifyAccess();
            if (_browser?.CoreWebView2 != null) _browser.CoreWebView2.WebMessageReceived -= Receive;
            _browser?.Dispose(); _source?.Dispose(); _browser = null; _source = null; _ready = null;
        }
        public void Dispose()
        {
            _dispatcher.VerifyAccess(); if (_disposed) return; _disposed = true;
            Fail(new ObjectDisposedException(nameof(BridgeGeometryRuntime)));
            ResetBrowser();
            _shapes.Clear(); _shapeUsage.Clear(); _shapeBytes = 0;
        }
    }
}
