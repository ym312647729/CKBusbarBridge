using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.Drivers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.WebTest
{
    internal static class CabinetReplanningProbe
    {
        internal static async Task<int> Run(string reportPath)
        {
            var report = new JArray();
            var schema = JObject.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Worker", "PreviewRuntime", "catalog", "original-cabinet-ui.json")));
            foreach (var scenario in new[] { "face-to-face", "side-to-side" })
            {
                var parameters = new JObject();
                foreach (var field in ((JObject)schema["scenarios"].Single(row => (string)row["key"] == scenario)["defaults"]).Properties())
                {
                    var segments = field.Name.Split('.'); var owner = parameters;
                    foreach (var segment in segments.Take(segments.Length - 1)) owner = owner[segment] as JObject ?? (JObject)(owner[segment] = new JObject());
                    owner[segments.Last()] = field.Value.DeepClone();
                }
                parameters["General"]["CabinetEccentricity"] = 0;
                parameters["General"]["InsulationDistance"] = 125;
                var request = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["revision"] = 1,
                    ["scenario"] = scenario, ["modelName"] = "偏心距重规划离线核查", ["parameters"] = parameters };
                using (var runtime = new BridgeGeometryRuntime())
                {
                    var before = await runtime.Compute(request);
                    var upstreamBefore = Read(before);
                    var provider = new PrototypeBusbarBridgeDesignProvider();
                    var initialResult = provider.TryCreatePlannedPreview(upstreamBefore, out var initial);
                    if (!initialResult.Success) throw new InvalidOperationException(initialResult.Message);
                    var editor = new BusbarBridgeDraftEditor(); editor.Load(initial);
                    var previous = editor.CaptureSavedSample();
                    await Inspect("初始偏心0", initial, upstreamBefore);
                    parameters["General"]["CabinetEccentricity"] = 50;
                    request["bridge"] = null; request["revision"] = 2;
                    var after = await runtime.Compute(request);
                    var upstreamAfter = Read(after);
                    var builder = new BridgeCabinetPlanBuilder();
                    var freshResult = provider.TryCreatePlannedPreview(upstreamAfter, out var fresh);
                    if (!freshResult.Success) throw new InvalidOperationException(freshResult.Message);
                    await Inspect("偏心50全新设计", fresh, upstreamAfter);
                    await TryInspect("偏心50自动联动", () => builder.Create(upstreamBefore, upstreamAfter, previous, null));
                    await TryInspect("偏心50旧重新推荐", () => builder.CreateDraftRecommendation(upstreamAfter, previous));

                    async Task TryInspect(string stage, Func<BridgeDesignSample> plan)
                    {
                        try { await Inspect(stage, plan(), upstreamAfter); }
                        catch (Exception error)
                        {
                            Console.WriteLine(scenario + " / " + stage + "：" + error.Message);
                            report.Add(new JObject { ["scenario"] = scenario, ["stage"] = stage, ["error"] = error.Message });
                        }
                    }
                    async Task Inspect(string stage, BridgeDesignSample sample, BridgeUpstreamSnapshot upstream)
                    {
                        var current = new BusbarBridgeDraftEditor(); current.Load(sample);
                        var geometry = BridgeParameterDraftGeometry.Export(current, upstream,
                            new BridgeCopperHolePattern { Location = "0,0", Style = "圆孔", Diameter = 13, SlotLength = 13 });
                        var geometryRequest = (JObject)request.DeepClone(); geometryRequest["bridge"] = geometry;
                        var result = await runtime.Compute(geometryRequest);
                        var record = new JObject { ["scenario"] = scenario, ["stage"] = stage, ["clearance"] = result["clearance"].DeepClone(),
                            ["planningIssues"] = JArray.FromObject(sample.Issues.Where(issue => issue.BlocksGeneration).Select(issue => issue.Message)),
                            ["frames"] = JArray.FromObject(sample.Objects.Where(node => node.Kind == DesignObjectKind.Frame)
                                .Select(node => new { node.Id, node.FrameTemplateKey, node.Length, node.X, node.Y, node.Z })),
                            ["warning"] = result["inspectionWarning"] };
                        report.Add(record);
                        File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
                        Console.WriteLine(scenario + " / " + stage + "：干涉=" + result["clearance"]?["assembly"]?["count"] +
                            "，相间=" + result["clearance"]?["phases"]?["minimumMm"] + "，对地=" + result["clearance"]?["grounded"]?["minimumMm"]);
                    }
                }
            }
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
            return 0;
        }
        private static BridgeUpstreamSnapshot Read(JObject result) => BridgeOfflineUpstreamAdapter.Read(
            (JObject)result["upstream"], "偏心距重规划离线核查.SLDASM", "默认", "铜");
    }
}
