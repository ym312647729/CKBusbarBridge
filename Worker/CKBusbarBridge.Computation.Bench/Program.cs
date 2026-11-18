using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CKBusbarBridge.Computation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run(string[] args)
    {
        var workerRoot = Path.GetFullPath(args[0]);
        var planner = Path.GetFullPath(args[1]);
        var schema = JObject.Parse(File.ReadAllText(Path.Combine(workerRoot, "PreviewRuntime", "catalog", "original-cabinet-ui.json")));
        var parameters = new JObject();
        foreach (var value in ((JObject)schema["scenarios"].First(row => (string)row["key"] == "face-to-face")["defaults"]).Properties())
        {
            var parts = value.Name.Split('.'); var owner = parameters;
            foreach (var part in parts.Take(parts.Length - 1)) owner = owner[part] as JObject ?? (JObject)(owner[part] = new JObject());
            owner[parts.Last()] = value.Value.DeepClone();
        }
        parameters["General"]["InsulationDistance"] = 125;
        var request = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["revision"] = 1,
            ["scenario"] = "face-to-face", ["modelName"] = "虚拟速度核查", ["parameters"] = parameters };
        var measurements = new JArray();
        using (var calculator = new PreviewCalculator(workerRoot))
        {
            var watch = Stopwatch.StartNew();
            var cabinet = await calculator.ComputeAsync(request);
            Console.WriteLine("首次柜体预览：{0:F0} ms，{1} 个板件", watch.Elapsed.TotalMilliseconds, cabinet["instances"].Count());
            var input = Path.Combine(Path.GetTempPath(), "ckbridge-benchmark-" + Guid.NewGuid().ToString("N") + ".json");
            JObject bridge;
            try
            {
                File.WriteAllText(input, cabinet["upstream"].ToString(Formatting.None), new UTF8Encoding(false));
                using (var plan = Process.Start(new ProcessStartInfo(planner, "--plan \"" + input + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) }))
                {
                    var output = await plan.StandardOutput.ReadToEndAsync(); var error = await plan.StandardError.ReadToEndAsync();
                    plan.WaitForExit(); if (plan.ExitCode != 0) throw new InvalidOperationException(error);
                    bridge = JObject.Parse(output);
                }
            }
            finally { File.Delete(input); }
            request["bridge"] = bridge;
            if (args.Length > 2 && args[2] == "--compare")
            {
                await CompareConcurrency(workerRoot, request);
                return;
            }
            for (var index = 0; index < 3; index++)
            {
                request["revision"] = index + 2;
                var calculation = Stopwatch.StartNew();
                var result = await calculator.ComputeAsync(request);
                var clearance = result["clearance"];
                if ((bool?)clearance?["passed"] != true || (int?)clearance?["assembly"]?["count"] != 0)
                    throw new InvalidOperationException("基准方案的绝缘或干涉校验未通过：" + clearance);
                var known = new JObject();
                foreach (var shape in result["instances"].Concat(result["interactionInstances"]))
                    known[(string)shape["geometryKey"]] = shape["geometrySignature"];
                request["knownShapes"] = known;
                var title = index == 0 ? "首次完整预览" : "重复完整预览" + index;
                if (index > 0 && (bool?)result["timings"]["inspectionReused"] != true)
                    throw new InvalidOperationException("未变更的几何没有复用已完成的校验结果。");
                var measurement = new JObject { ["stage"] = title, ["elapsedMs"] = calculation.Elapsed.TotalMilliseconds,
                    ["calculatorTimings"] = result["timings"], ["instances"] = result["instances"].Count(),
                    ["clearanceMm"] = clearance["grounded"]["minimumMm"], ["interferences"] = clearance["assembly"]["count"] };
                measurements.Add(measurement);
                Console.WriteLine("{0}：{1:F0} ms；净距 {2:F2} mm，干涉 {3}", title, calculation.Elapsed.TotalMilliseconds,
                    (double)clearance["grounded"]["minimumMm"], clearance["assembly"]["count"]);
            }
            Console.WriteLine("从进入到首次完整预览（含规划）：{0:F0} ms", watch.Elapsed.TotalMilliseconds -
                measurements.Skip(1).Sum(row => (double)row["elapsedMs"]));
            request["parameters"]["General"]["InsulationDistance"] = 124;
            var changed = await calculator.ComputeAsync(request);
            if ((bool?)changed["timings"]["inspectionReused"] == true || (double)changed["clearance"]["requiredMm"] != 124)
                throw new InvalidOperationException("绝缘设置变化后未重新执行实体校验。");
            Console.WriteLine("绝缘设置变化后重新完成实体校验。");
            foreach (var scenario in new[] { "side-to-side", "face-to-wall" })
                foreach (var orientation in scenario == "face-to-wall" ? new[] { "CabinetFrontToWall", "CabinetRearToWall" } : new[] { "CabinetFrontToWall" })
                {
                    var next = (JObject)request.DeepClone(); next.Remove("bridge"); next.Remove("knownShapes");
                    next["scenario"] = scenario; next["orientation"] = orientation;
                    var values = new JObject();
                    foreach (var field in ((JObject)schema["scenarios"].First(row => (string)row["key"] == scenario)["defaults"]).Properties())
                    {
                        var parts = field.Name.Split('.'); var parent = values;
                        foreach (var part in parts.Take(parts.Length - 1)) parent = parent[part] as JObject ?? (JObject)(parent[part] = new JObject());
                        parent[parts.Last()] = field.Value.DeepClone();
                    }
                    values["General"]["InsulationDistance"] = 125; next["parameters"] = values;
                    var result = await calculator.ComputeAsync(next);
                    if (scenario == "face-to-wall")
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ckbridge-wall-" + orientation + ".json"),
                            result["upstream"].ToString(Formatting.None), new UTF8Encoding(false));
                    if (!result["instances"].Any() || !result["dimensions"].Any(row => (string)row["field"] ==
                        (scenario == "face-to-wall" ? "General.CabinetToWallDistance" : "General.CabinetSpacing")))
                        throw new InvalidOperationException(scenario + " 未输出完整柜体、基座或间距。");
                    Console.WriteLine("{0}：{1} 个板件，离线计算通过。",
                        scenario == "side-to-side" ? "侧对侧" : orientation == "CabinetFrontToWall" ? "柜前向墙" : "柜后向墙",
                        result["instances"].Count());
                }
        }
        File.WriteAllText(args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "ckbridge-preview-benchmark.json"),
            measurements.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    private static async Task CompareConcurrency(string workerRoot, JObject fullRequest)
    {
        JToken expectedClearance = null;
        JArray expectedShapes = null;
        var measurements = new JArray();
        foreach (var round in Enumerable.Range(1, 3))
            foreach (var workers in new[] { 1, CalculationConcurrency.MaximumWorkers })
                using (var calculator = new PreviewCalculator(workerRoot))
                {
                    var cabinet = (JObject)fullRequest.DeepClone(); cabinet.Remove("bridge");
                    cabinet["maximumWorkers"] = workers;
                    var watch = Stopwatch.StartNew();
                    await calculator.ComputeAsync(cabinet);
                    var cabinetMs = watch.Elapsed.TotalMilliseconds;
                    var request = (JObject)fullRequest.DeepClone(); request["maximumWorkers"] = workers;
                    watch.Restart();
                    var result = await calculator.ComputeAsync(request);
                    var elapsedMs = watch.Elapsed.TotalMilliseconds;
                    var shapes = new JArray(result["instances"].Select(row => new JObject
                    { ["id"] = row["id"], ["signature"] = row["geometrySignature"], ["transform"] = row["transform"] }));
                    if (expectedClearance == null) { expectedClearance = result["clearance"].DeepClone(); expectedShapes = shapes; }
                    if (!JToken.DeepEquals(expectedClearance, result["clearance"]) || !JToken.DeepEquals(expectedShapes, shapes))
                        throw new InvalidOperationException("并行结果与单线程的完整几何或净距、干涉结果不一致。");
                    measurements.Add(new JObject { ["round"] = round, ["workers"] = workers, ["cabinetMs"] = cabinetMs,
                        ["fullMs"] = elapsedMs, ["timings"] = result["timings"] });
                    Console.WriteLine("第{0}轮，最大并发{1}：柜体{2:F0} ms，完整预览{3:F0} ms；与单线程结果完全一致。",
                        round, workers, cabinetMs, elapsedMs);
                }
        foreach (var workers in new[] { 1, CalculationConcurrency.MaximumWorkers })
            Console.WriteLine("最大并发{0}：完整预览中位耗时{1:F0} ms。", workers,
                measurements.Where(row => (int)row["workers"] == workers).Select(row => (double)row["fullMs"]).OrderBy(value => value).ElementAt(1));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ckbridge-parallel-benchmark.json"),
            measurements.ToString(Formatting.Indented), new UTF8Encoding(false));
    }
}
