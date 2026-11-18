using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.Services;
using CKBusbarBridge.Drivers;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.WebTest
{
    internal static class WallStreamingProbe
    {
        internal static int CheckPrefix(JObject input)
        {
            var snapshot = BridgeOfflineUpstreamAdapter.Read(input, "虚拟面对墙.SLDASM", "默认", "铜").FaceToWall;
            var planning = new FaceToWallPreviewPlanning().CreateInput(snapshot);
            var objects = snapshot.FixedObjects.ToList();
            var routes = new FaceToWallPreviewRouteFactory().Create(snapshot, planning.PrefixGroups, objects);
            var start = snapshot.Cabinet.Opening.X;
            var bottom = snapshot.Cabinet.Opening.Y - snapshot.Cabinet.OpeningHeight / 2 + 40;
            var violations = 0;
            foreach (var route in routes)
                foreach (var solid in route.PhysicalSolids.Where(solid => solid.Max(point => point[0]) >= start - .001 &&
                    solid.Min(point => point[0]) <= start + 1 && solid.Min(point => point[2]) < bottom - .01))
                {
                    violations++;
                    Console.WriteLine("柜端凸单元：入口差 {0:R}～{1:R}，高度 {2:R}～{3:R}",
                        solid.Min(point => point[0]) - start, solid.Max(point => point[0]) - start,
                        solid.Min(point => point[2]), solid.Max(point => point[2]));
                }
            Console.WriteLine(violations == 0 ? "柜端连接段未从低于通口的位置越入基架。" : "柜端连接段存在通口越界。");
            return violations == 0 ? 0 : 1;
        }
        internal static int Run(JObject input, string pluginPath, string probeIdentity)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var upstream = BridgeOfflineUpstreamAdapter.Read(input, "虚拟面对墙.SLDASM", "默认", "铜");
            using (var model = new BusbarBridgeDesignWindowVM(new PrototypeBusbarBridgeDesignProvider(),
                new BusbarBridgeDraftEditor(), text => true, new FrozenReader(upstream)))
            {
                var plugin = Assembly.LoadFrom(Path.GetFullPath(pluginPath));
                var authorization = plugin.GetType("CKBusbarBridge.Services.BusbarWorkerAuthorization", true)
                    .GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
                model.ResolveWorkerAuthorization = () => (string)authorization.Invoke(null, null);
                model.ReadHangerContext = (snapshot, sample) =>
                {
                    var roof = input["hangerRoof"];
                    return roof?["faces"] is JArray faces && faces.Count != 0 ? sample.WithHangerRoof(
                        new BridgeHangerRoof((string)roof["identity"], faces.Select(face => new BridgeHangerRoofFace(
                            face["point"].Values<double>().ToArray(), face["normal"].Values<double>().ToArray()))), null) : sample;
                };
                var watch = Stopwatch.StartNew(); long first = -1;
                Exception exportFailure = null;
                void SaveGeometry(string stage)
                {
                    if (model.Editor.HasBlockingIssues) throw new InvalidOperationException("发布方案仍有阻断性校验问题。");
                    var bridge = BridgeParameterDraftGeometry.Export(model.Editor, upstream, new BridgeCopperHolePattern
                    { Location = "0,0", Style = "圆孔", Diameter = 13, SlotLength = 13 });
                    if ((string)bridge?["status"] != "ready" || bridge["frameDrives"]?.Any() != true)
                        throw new InvalidOperationException("发布方案缺少完整的可预览铜排或基架驱动。");
                    var output = Path.Combine(Path.GetTempPath(), probeIdentity + "." + stage + "-bridge.json");
                    File.WriteAllText(output, bridge.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false));
                    Console.WriteLine("预览几何已输出：" + output);
                }
                model.ModelPreviewReady += (sender, args) =>
                {
                    if (first >= 0 || model.Editor.CurrentSample == null || !model.Editor.CurrentSample.Routes.Any(route => route.IsPhysicalGeometry)) return;
                    first = watch.ElapsedMilliseconds;
                    try { SaveGeometry("first"); }
                    catch (Exception error) { exportFailure = error; }
                    Console.WriteLine("首个通过校验的铜排方案已发布：{0} ms；后台仍在计算：{1}", first, model.PlanningBusy ? "是" : "否");
                };
                model.ReadUpstream();
                var loop = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (sender, args) =>
                {
                    if (model.PlanningCompletion.IsCompleted || watch.ElapsedMilliseconds > 100000) loop.Continue = false;
                };
                timer.Start(); Dispatcher.PushFrame(loop); timer.Stop();
                Console.WriteLine("计算结束：{0} ms；{1}", watch.ElapsedMilliseconds, model.Message);
                if (!model.PlanningCompletion.IsCompleted || first < 0 || first >= watch.ElapsedMilliseconds - 500)
                    throw new InvalidOperationException("未在搜索结束前显示已发布的有效铜排方案。");
                if (watch.ElapsedMilliseconds < 59000)
                    throw new InvalidOperationException("面对墙搜索未执行到 1 分钟。");
                if (exportFailure != null) throw new InvalidOperationException("首次发布的几何导出失败。", exportFailure);
                SaveGeometry("final");
                return 0;
            }
        }
        private sealed class FrozenReader : IBridgeUpstreamReader
        {
            private readonly BridgeUpstreamSnapshot _snapshot;
            internal FrozenReader(BridgeUpstreamSnapshot snapshot) { _snapshot = snapshot; }
            public BridgeUpstreamSnapshot Read() => _snapshot;
        }
    }
}
