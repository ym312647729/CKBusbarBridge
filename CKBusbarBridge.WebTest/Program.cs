using CKBusbarBridge.DesignPreview;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace CKBusbarBridge.WebTest
{
    internal static class Program
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly string OutputFolder = AppDomain.CurrentDomain.BaseDirectory;

        [STAThread]
        internal static int Main(string[] args)
        {
            Console.OutputEncoding = Utf8;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveCalculationAssembly;
            try
            {
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--stream-wall")
                    return WallStreamingProbe.Run(JObject.Parse(File.ReadAllText(args[1], Utf8)),
                        args.Length == 3 ? args[2] : Path.Combine(OutputFolder, "CKBusbarBridge.dll"), Path.GetFileNameWithoutExtension(args[1]));
                if (args.Length == 2 && args[0] == "--wall-prefix")
                    return WallStreamingProbe.CheckPrefix(JObject.Parse(File.ReadAllText(args[1], Utf8)));
                if (args.Length == 2 && args[0] == "--plan")
                {
                    Console.WriteLine(Plan(JObject.Parse(File.ReadAllText(args[1], Utf8))).ToString(Formatting.None));
                    return 0;
                }
                if (!Directory.Exists(Path.Combine(OutputFolder, "Worker", "PreviewRuntime")))
                    throw new DirectoryNotFoundException("缺少同一构建的 PreviewRuntime 目录。");
                using (var listener = new HttpListener())
                {
                    var portSelector = new TcpListener(IPAddress.Loopback, 0);
                    portSelector.Start();
                    var port = ((IPEndPoint)portSelector.LocalEndpoint).Port;
                    portSelector.Stop();
                    var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                    listener.Prefixes.Add(url);
                    listener.Start();
                    Console.WriteLine("母线桥虚拟测试： " + url + "  （关闭此窗口即可结束）");
                    if (!args.Contains("--no-browser"))
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    while (listener.IsListening) Serve(listener.GetContext(), url);
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        private static Assembly ResolveCalculationAssembly(object sender, ResolveEventArgs request)
        {
            var requested = new AssemblyName(request.Name);
            if (requested.Name != "CKBusbarBridge.Computation") return null;
            var path = Path.Combine(OutputFolder, "Worker", requested.Name + ".dll");
            if (!File.Exists(path) || AssemblyName.GetAssemblyName(path).FullName != requested.FullName) return null;
            return Assembly.LoadFrom(path);
        }

        private static JObject Plan(JObject upstream)
        {
            if ((string)upstream["scenario"] != "face-to-face")
                throw new InvalidOperationException("测试器仅开放面对面常规场景。");
            var snapshot = BridgeOfflineUpstreamAdapter.Read(upstream, "虚拟测试.SLDASM", "默认", "铜");
            var plan = new PrototypeBusbarBridgeDesignProvider().TryCreatePlannedPreview(snapshot, out var sample);
            if (!plan.Success || sample == null) throw new InvalidOperationException(plan.Message ?? "无法生成面对面方案。");
            if (sample.Objects.Any(item => item.Kind == DesignObjectKind.Frame && item.FrameTemplateKey != "Regular"))
                throw new InvalidOperationException("测试器仅开放面对面常规基架，请将柜间偏心设为 0 并保持不换相。");
            var roof = upstream["hangerRoof"];
            if (roof?["faces"] is JArray faces && faces.Count != 0)
                sample = sample.WithHangerRoof(new BridgeHangerRoof((string)roof["identity"], faces.Select(face =>
                    new BridgeHangerRoofFace(face["point"].Values<double>().ToArray(), face["normal"].Values<double>().ToArray()))), null);
            var editor = new BusbarBridgeDraftEditor();
            editor.Load(sample);
            // Virtual fit check uses a synthetic round test hole only. No factory process library is loaded.
            var result = BridgeParameterDraftGeometry.Export(editor, snapshot, new BridgeCopperHolePattern
            {
                Location = "0,0", Style = "圆孔", Diameter = 13, SlotLength = 13
            });
            result["planningMessages"] = JArray.FromObject(editor.PlanningIssues.Where(issue => issue.BlocksGeneration).Select(issue => issue.Message));
            return result;
        }

        private static void Serve(HttpListenerContext context, string url)
        {
            using (var response = context.Response)
            {
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                try
                {
                    var request = context.Request;
                    var path = Uri.UnescapeDataString(request.Url.AbsolutePath.TrimStart('/'));
                    if (path == "api/settings" || path == "api/plan")
                    {
                        if (request.HttpMethod == "POST" && (request.Headers["Origin"] != url.TrimEnd('/') ||
                              request.ContentLength64 < 0 || request.ContentLength64 > 2000000))
                            throw new InvalidOperationException("本地请求来源或长度无效。");
                        if (path == "api/settings" && request.HttpMethod == "GET")
                            Write(response, JObject.FromObject(new { clearanceMm = InsulationDistanceMetadata.ConfiguredMillimetres }));
                        else if (request.HttpMethod == "POST")
                        {
                            using (var input = new StreamReader(request.InputStream, Utf8))
                            {
                                var data = JObject.Parse(input.ReadToEnd());
                                if (path == "api/plan") Write(response, Plan(data));
                                else
                                {
                                    var clearance = (double?)data["clearanceMm"] ?? 0;
                                    InsulationDistanceMetadata.SaveDefault(clearance);
                                    Write(response, JObject.FromObject(new { clearanceMm = clearance }));
                                }
                            }
                        }
                        else response.StatusCode = 405;
                        return;
                    }
                    if (request.HttpMethod != "GET") { response.StatusCode = 405; return; }
                    var isRuntime = path.StartsWith("runtime/", StringComparison.Ordinal);
                    var root = isRuntime ? Path.Combine(OutputFolder, "Worker", "PreviewRuntime") : Path.Combine(OutputFolder, "Web");
                    var asset = Path.GetFullPath(Path.Combine(root, isRuntime ? path.Substring(8) : path.Length == 0 ? "index.html" : path));
                    if (!asset.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(asset))
                    { response.StatusCode = 404; return; }
                    switch (Path.GetExtension(asset))
                    {
                        case ".html": response.ContentType = "text/html; charset=utf-8"; break;
                        case ".mjs":
                        case ".js": response.ContentType = "text/javascript; charset=utf-8"; break;
                        case ".css": response.ContentType = "text/css; charset=utf-8"; break;
                        case ".json": response.ContentType = "application/json; charset=utf-8"; break;
                        case ".wasm": response.ContentType = "application/wasm"; break;
                        default: response.StatusCode = 404; return;
                    }
                    using (var input = File.OpenRead(asset))
                    {
                        response.ContentLength64 = input.Length;
                        input.CopyTo(response.OutputStream);
                    }
                }
                catch (IOException) { }
                catch (HttpListenerException) { }
                catch (Exception error)
                {
                    response.StatusCode = 400;
                    Write(response, JObject.FromObject(new { error = error.Message }));
                }
            }
        }

        private static void Write(HttpListenerResponse response, JObject result)
        {
            var bytes = Utf8.GetBytes(result.ToString(Formatting.None));
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
