using System;
using System.Diagnostics;
using System.Globalization;
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
    internal static class ReferenceCasesProbe
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        internal static async Task<int> Run(string inputPath, string reportPath)
        {
            var suite = JObject.Parse(File.ReadAllText(inputPath));
            var results = new JArray();
            var report = new JObject { ["schema"] = "bridge-reference-results/1", ["package"] = BridgeGeometryRuntime.PackageVersion,
                ["fixtureSha256"] = Hash(File.ReadAllBytes(inputPath)), ["sources"] = suite["sources"].DeepClone(),
                ["nativeSolidWorksTest"] = false, ["cases"] = results };
            using (var runtime = new BridgeGeometryRuntime())
            {
                foreach (var input in suite["cases"].OfType<JObject>())
                {
                    var watch = Stopwatch.StartNew();
                    var id = (string)input["id"]; var scenario = (string)input["scenario"];
                    var record = new JObject { ["id"] = id, ["source"] = input["source"], ["sourceIndex"] = input["sourceIndex"],
                        ["scenario"] = scenario, ["sourceStatus"] = input["sourceStatus"], ["historicalOutcome"] = input["historicalOutcome"] };
                    results.Add(record);
                    Console.WriteLine("开始 {0}/{1}：{2}", results.Count, suite["count"], id);
                    var stage = "输入参数";
                    try
                    {
                        if (scenario == null || !(input["parameters"] is JObject raw) || !raw.HasValues)
                        { record["outcome"] = "source-incomplete"; record["error"] = "原测试记录没有可计算的场景或参数，未补造输入。"; continue; }
                        var parameterObject = BridgeCabinetParameterBinding.Restore(scenario, raw);
                        var parameters = BridgeParameterSnapshotBinding.Capture(parameterObject);
                        record["inputSha256"] = Hash(Utf8.GetBytes(parameters.ToString(Formatting.None)));
                        var orientation = (string)input["orientation"];
                        if (orientation == "柜前向墙") orientation = "CabinetFrontToWall";
                        if (orientation == "柜后向墙") orientation = "CabinetRearToWall";
                        if (scenario == "face-to-wall" && string.IsNullOrWhiteSpace(orientation))
                            throw new InvalidOperationException("原面对墙记录缺少柜前／柜后朝向，不能猜测模板。");
                        var request = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["revision"] = 1,
                            ["scenario"] = scenario, ["orientation"] = orientation, ["modelName"] = id, ["parameters"] = parameters };
                        stage = "柜体和基座计算";
                        var cabinet = await runtime.Compute(request);
                        var upstream = BridgeOfflineUpstreamAdapter.Read((JObject)cabinet["upstream"], id + ".SLDASM", "默认", "铜");
                        JObject bridge;
                        if (scenario == "face-to-wall")
                        {
                            stage = "面对墙一分钟规划";
                            bridge = await PlanWall(id, (JObject)cabinet["upstream"], record);
                        }
                        else
                        {
                            stage = "重新设计母排和基架";
                            var provider = new PrototypeBusbarBridgeDesignProvider();
                            var planned = provider.TryCreatePlannedPreview(upstream, out var sample);
                            if (!planned.Success || sample == null) throw new InvalidOperationException(planned.Message);
                            var editor = new BusbarBridgeDraftEditor(); editor.Load(sample);
                            record["planningIssues"] = Issues(editor);
                            if (input["bridgeEdits"] is JArray edits && edits.Count != 0)
                            {
                                stage = "桥体编辑操作";
                                foreach (var edit in edits.OfType<JObject>()) ApplyEdit(editor, edit);
                                var editRequest = (JObject)request.DeepClone(); editRequest["bridge"] = Export(editor, upstream);
                                var edited = await runtime.Compute(editRequest);
                                record["editedClearance"] = edited["clearance"].DeepClone();
                                record["editCount"] = edits.Count;
                            }
                            stage = "按当前输入全新规划";
                            var current = editor.CaptureSavedSample();
                            try
                            {
                                var fresh = new BridgeCabinetPlanBuilder().CreateDraftRecommendation(upstream, current);
                                editor.Load(fresh);
                                if (fresh.SavedLockedObjectIds.Count != 0 || fresh.SavedManualInsulatorIds.Count != 0 || fresh.SavedManualJointIds.Count != 0)
                                    throw new InvalidOperationException("全新规划仍保留了旧人工约束。");
                            }
                            catch (InvalidOperationException error) { record["replanValidationFailure"] = error.Message; }
                            bridge = Export(editor, upstream);
                            record["planningIssues"] = Issues(editor);
                        }
                        stage = "完整实体及125毫米净距检查";
                        request["bridge"] = bridge;
                        var full = await runtime.Compute(request);
                        record["clearance"] = full["clearance"].DeepClone();
                        record["instances"] = full["instances"].Count();
                        record["warning"] = full["inspectionWarning"];
                        record["frames"] = bridge["objects"].Count(row => (string)row["kind"] == "Frame");
                        record["outcome"] = (bool?)full["clearance"]?["passed"] == true &&
                            (int?)full["clearance"]?["assembly"]?["count"] == 0 && record["replanValidationFailure"] == null &&
                            !(record["planningIssues"] as JArray ?? new JArray()).Any()
                            ? "passed" : "blocked";
                    }
                    catch (Exception error)
                    { record["outcome"] = "rejected"; record["stage"] = stage; record["error"] = error.Message; }
                    finally
                    {
                        record["elapsedMs"] = watch.ElapsedMilliseconds;
                        report["summary"] = JObject.FromObject(results.GroupBy(row => (string)row["outcome"]).ToDictionary(group => group.Key, group => group.Count()));
                        File.WriteAllText(reportPath, report.ToString(Formatting.Indented), Utf8);
                        Console.WriteLine("完成 {0}/{1}：{2}；{3}；{4} ms", results.Count, suite["count"], id, record["outcome"], watch.ElapsedMilliseconds);
                    }
                }
            }
            Console.WriteLine("全部记录已执行：" + report["summary"].ToString(Formatting.None));
            return results.Count == (int)suite["count"] ? 0 : 1;
        }
        private static JArray Issues(BusbarBridgeDraftEditor editor) => JArray.FromObject(
            editor.PlanningIssues.Concat(editor.ClearanceIssues).Concat(editor.FrameConnectionIssues)
                .Where(issue => issue.BlocksGeneration).Select(issue => issue.Message).Distinct());
        private static JObject Export(BusbarBridgeDraftEditor editor, BridgeUpstreamSnapshot upstream) => BridgeParameterDraftGeometry.Export(
            editor, upstream, new BridgeCopperHolePattern { Location = "0,0", Style = "圆孔", Diameter = 13, SlotLength = 13 });
        private static string Hash(byte[] data)
        { using (var hash = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        private static void ApplyEdit(BusbarBridgeDraftEditor editor, JObject edit)
        {
            var action = (string)edit["action"]; var delta = (double?)edit["deltaMm"] ?? 0;
            BridgeEditResult result;
            if (action == "move-breakpoint")
            {
                var point = editor.EffectivePlan.First(node => node.Kind == DesignObjectKind.Breakpoint && node.Name == "断点" + (int)edit["breakpointNumber"]);
                result = editor.SetText(point.Id, DesignField.X, (point.X + delta).ToString("R", CultureInfo.InvariantCulture), false);
            }
            else if (action == "add-breakpoint")
            {
                var section = editor.CopperSections[(int)edit["sectionIndex"] - 1];
                var before = editor.EffectivePlan.Select(node => node.Id).ToArray();
                result = editor.AddBreakpoint(section.Id);
                if (result.Success && delta != 0)
                {
                    var point = editor.EffectivePlan.First(node => node.Kind == DesignObjectKind.Breakpoint && !before.Contains(node.Id));
                    result = editor.SetText(point.Id, DesignField.X, (point.X + delta).ToString("R", CultureInfo.InvariantCulture), false);
                }
            }
            else
            {
                var frame = editor.EffectivePlan.Where(node => node.Kind == DesignObjectKind.Frame).OrderBy(node => node.FramePositionIndex).ElementAt((int)edit["framePosition"] - 1);
                if (action == "set-frame-length")
                    result = editor.SetText(frame.Id, DesignField.Length, (frame.Length + delta).ToString("R", CultureInfo.InvariantCulture), false);
                else if (action == "move-insulator")
                {
                    var positions = editor.EffectivePlan.Where(node => node.ParentId == frame.Id && node.Kind == DesignObjectKind.Insulator)
                        .GroupBy(node => node.InsulatorGroupId).OrderBy(group => group.First().X).ToArray();
                    var insulator = positions[(int)edit["insulatorPosition"] - 1].First();
                    result = editor.SetInsulatorPosition(insulator.Id, insulator.X + frame.Length / 2 + delta, false);
                }
                else throw new InvalidOperationException("未支持原回归操作：" + action);
            }
            if (!result.Success) throw new InvalidOperationException("原回归操作未通过：" + result.Message);
        }
        private static async Task<JObject> PlanWall(string id, JObject upstream, JObject record)
        {
            var prefix = "ckbridge-reference-" + id;
            var input = Path.Combine(Path.GetTempPath(), prefix + ".json");
            File.WriteAllText(input, upstream.ToString(Formatting.None), Utf8);
            var info = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CKBusbarBridge.WebTest.exe"),
                "--stream-wall \"" + input + "\" \"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CKBusbarBridge.dll") + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
            using (var process = Process.Start(info))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
                var finished = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(finished, Task.Delay(110000)) != finished)
                { if (!process.HasExited) process.Kill(); throw new TimeoutException("此面对墙用例在110秒内未完成，已停止自有测试子进程。"); }
                record["wallOutput"] = await outputTask;
                var error = await errorTask;
                if (process.ExitCode != 0) throw new InvalidOperationException(error + "；" + (string)record["wallOutput"]);
            }
            return JObject.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), prefix + ".final-bridge.json")));
        }
    }
}
