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
        if (args.Length > 2 && args[2] == "--transport-check")
        { CheckTransportContract(); return; }
        if (args.Length > 3 && args[2] == "--wall-mesh")
        {
            await CheckWallMesh(workerRoot, schema, args[3], args.Length > 4 ? args[4] : "CabinetFrontToWall",
                args.Length > 5 ? int.Parse(args[5]) : CalculationConcurrency.MaximumWorkers);
            return;
        }
        var parameters = new JObject();
        foreach (var value in ((JObject)schema["scenarios"].First(row => (string)row["key"] == "face-to-face")["defaults"]).Properties())
        {
            var parts = value.Name.Split('.'); var owner = parameters;
            foreach (var part in parts.Take(parts.Length - 1)) owner = owner[part] as JObject ?? (JObject)(owner[part] = new JObject());
            owner[parts.Last()] = value.Value.DeepClone();
        }
        parameters["General"]["InsulationDistance"] = 125;
        var request = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["revision"] = 1,
            ["scenario"] = "face-to-face", ["modelName"] = "虚拟速度核查", ["parameters"] = parameters, ["includePartSources"] = false };
        var measurements = new JArray();
        IDisposable viewportProbe = null;
        try
        {
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
                if (args.Length > 3 && args[2] == "--distribution-compare")
                {
                    await CompareDistribution(args[3], workerRoot, request);
                    return;
                }
                if (args.Length > 2 && args[2] == "--compare")
                {
                    await CompareConcurrency(workerRoot, request);
                    return;
                }
                double firstCompleteElapsedMs = 0;
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
                    if (index == 0) firstCompleteElapsedMs = watch.Elapsed.TotalMilliseconds;
                    CheckViewportFrame(result, ref viewportProbe);
                }
                Console.WriteLine("从进入到首次完整预览（含规划）：{0:F0} ms", firstCompleteElapsedMs);
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
        finally { viewportProbe?.Dispose(); }
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

    private static async Task CompareDistribution(string baselineRoot, string optimizedRoot, JObject fullRequest)
    {
        JToken expectedGeometry = null, expectedClearance = null, expectedDimensions = null;
        var measurements = new JArray();
        foreach (var round in Enumerable.Range(1, 3))
            foreach (var root in round % 2 == 1 ? new[] { baselineRoot, optimizedRoot } : new[] { optimizedRoot, baselineRoot })
                using (var calculator = new PreviewCalculator(root))
                {
                    var cabinet = (JObject)fullRequest.DeepClone(); cabinet.Remove("bridge");
                    cabinet["profileScheduling"] = true;
                    await calculator.ComputeAsync(cabinet);
                    var request = (JObject)fullRequest.DeepClone(); request["profileScheduling"] = true;
                    var watch = Stopwatch.StartNew();
                    var result = await calculator.ComputeAsync(request);
                    var elapsedMs = watch.Elapsed.TotalMilliseconds;
                    var shapes = new Dictionary<string, JObject>();
                    var geometryFields = new[] { "positions", "quads", "triangles", "edgePositions", "edges" };
                    var geometry = new JArray(new[] { "instances", "interactionInstances" }.Select(name =>
                        new JArray(result[name].Select(row =>
                        {
                            var key = (string)row["geometryKey"];
                            if (row["positions"] != null) shapes[key] = (JObject)row;
                            var source = shapes[key];
                            var entry = new JObject();
                            foreach (var field in new[] { "id", "designObjectId", "groupId", "memberIds", "displayName", "sourcePath",
                                "phase", "category", "appearanceRole", "transform", "outlineOnly" }) entry[field] = row[field]?.DeepClone();
                            foreach (var field in geometryFields) entry[field] = source[field]?.DeepClone();
                            return entry;
                        }))));
                    if (expectedGeometry == null)
                    { expectedGeometry = geometry; expectedClearance = result["clearance"].DeepClone(); expectedDimensions = result["dimensions"].DeepClone(); }
                    if (!JToken.DeepEquals(expectedGeometry, geometry) || !JToken.DeepEquals(expectedClearance, result["clearance"]) ||
                        !JToken.DeepEquals(expectedDimensions, result["dimensions"]))
                        throw new InvalidOperationException("任务分配优化改变了几何、安装、标注或安全检查结果。");
                    var optimized = root == optimizedRoot;
                    measurements.Add(new JObject { ["round"] = round, ["optimized"] = optimized, ["fullMs"] = elapsedMs,
                        ["timings"] = result["timings"], ["transport"] = result["transportTimings"] });
                    Console.WriteLine("第{0}轮，{1}：完整预览{2:F0} ms，几何、安装、标注和实体检查完全一致。",
                        round, optimized ? "优化后" : "优化前", elapsedMs);
                }
        foreach (var optimized in new[] { false, true })
            Console.WriteLine("{0}中位耗时：{1:F0} ms。", optimized ? "优化后" : "优化前",
                measurements.Where(row => (bool)row["optimized"] == optimized).Select(row => (double)row["fullMs"]).OrderBy(value => value).ElementAt(1));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ckbridge-distribution-benchmark.json"), measurements.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    private static void CheckTransportContract()
    {
        var expand = typeof(PreviewCalculator).Assembly.GetType("CKBusbarBridge.Computation.MeshTransport", true)
            .GetMethod("Expand", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var coordinates = new[] { 0f, -0f, 1f / 3f, -123456.789f, 1e-40f, float.MaxValue };
        var indices = new[] { 0u, 1u, uint.MaxValue };
        string Encode(Array values)
        { var bytes = new byte[values.Length * 4]; System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); }
        JObject Payload(string encoding, string name, string data) => new JObject { ["instances"] = new JArray(new JObject
            { ["id"] = "保留实例身份", ["packedGeometry"] = new JObject { ["encoding"] = encoding,
                ["fields"] = new JObject { [name] = data } } }) };
        var response = Payload("mesh32-le/1", "positions", Encode(coordinates));
        response["instances"][0]["packedGeometry"]["fields"]["edges"] = Encode(indices);
        expand.Invoke(null, new object[] { response });
        if (!response["instances"][0]["positions"].Values<double>().SequenceEqual(coordinates.Select(value => (double)value)) ||
            !response["instances"][0]["edges"].Values<ulong>().SequenceEqual(indices.Select(value => (ulong)value)) ||
            response["instances"][0]["packedGeometry"] != null || (string)response["instances"][0]["id"] != "保留实例身份")
            throw new InvalidOperationException("网格解码改变原精度坐标、无符号索引或实例身份。");
        foreach (var malformed in new[] { Payload("未知格式", "positions", ""), Payload("mesh32-le/1", "positions", "AA=="),
            Payload("mesh32-le/1", "positions", Encode(new[] { float.NaN })), Payload("mesh32-le/1", "未知字段", "") })
        {
            try { expand.Invoke(null, new object[] { malformed }); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidOperationException) { continue; }
            throw new InvalidOperationException("非法网格传输没有被拒绝。");
        }
        Console.WriteLine("独立DLL解码核查通过：原精度、极值索引、实例身份及非法报文拒绝。");
    }

    private static void CheckViewportFrame(JObject response, ref IDisposable runtime)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        if (runtime == null)
        {
            var design = System.Reflection.Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CKBusbarBridge.DesignUI.dll"));
            runtime = (IDisposable)Activator.CreateInstance(design.GetType("CKBusbarBridge.Prototype.BridgeGeometryRuntime", true), true);
        }
        var frame = runtime.GetType().GetMethod("CreateFrame", flags).Invoke(runtime, new object[] { Guid.NewGuid(), 1L, response });
        foreach (var collection in new[] { "Instances", "InteractionInstances" })
        {
            var instances = (System.Collections.IEnumerable)frame.GetType().GetProperty(collection, flags).GetValue(frame);
            if (instances.Cast<object>().Count() != response[collection == "Instances" ? "instances" : "interactionInstances"].Count())
                throw new InvalidOperationException("视口冻结帧丢失了网格引用实例。");
        }
        Console.WriteLine("原视口冻结帧解包通过，重复网格与后续缓存引用可用；未调用原生视口。");
    }

    private static async Task CheckWallMesh(string workerRoot, JObject schema, string bridgePath, string orientation, int workers)
    {
        var parameters = new JObject();
        foreach (var value in ((JObject)schema["scenarios"].First(row => (string)row["key"] == "face-to-wall")["defaults"]).Properties())
        {
            var parts = value.Name.Split('.'); var owner = parameters;
            foreach (var part in parts.Take(parts.Length - 1)) owner = owner[part] as JObject ?? (JObject)(owner[part] = new JObject());
            owner[parts.Last()] = value.Value.DeepClone();
        }
        parameters["General"]["InsulationDistance"] = 125;
        var request = new JObject { ["sessionId"] = "面对墙实体检查", ["revision"] = 1, ["scenario"] = "face-to-wall",
            ["modelName"] = "面对墙实体检查", ["orientation"] = orientation, ["parameters"] = parameters,
            ["bridge"] = JObject.Parse(File.ReadAllText(bridgePath)), ["checkClearance"] = true,
            ["maximumWorkers"] = workers, ["includePartSources"] = false };
        using (var calculator = new PreviewCalculator(workerRoot))
        {
            var result = await calculator.ComputeAsync(request);
            Console.WriteLine("面对墙完整实体校验：" + result["clearance"].ToString(Formatting.None));
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ckbridge-wall-mesh-result.json"), result.ToString(Formatting.None), new UTF8Encoding(false));
            if ((bool?)result["clearance"]?["passed"] != true || (int?)result["clearance"]?["assembly"]?["count"] != 0)
                throw new InvalidOperationException("面对墙完整实体未通过125 mm绝缘净距或干涉检查。");
            if (orientation == "CabinetFrontToWall")
            {
                var changed = (JObject)request.DeepClone(); changed.Remove("bridge");
                changed["parameters"]["Cabinet"]["FrontHole"]["BusbarCenterToCabinetEdgeDistance"] =
                    (double)parameters["Cabinet"]["FrontHole"]["BusbarCenterToCabinetEdgeDistance"] + 20;
                var shifted = (await calculator.ComputeAsync(changed))["upstream"];
                var baseline = result["upstream"]["sides"][0];
                if (!JToken.DeepEquals(baseline["opening"], shifted["sides"][0]["opening"]) ||
                    JToken.DeepEquals(baseline["extensions"][0]["interfacePoint"], shifted["sides"][0]["extensions"][0]["interfacePoint"]))
                    throw new InvalidOperationException("铜排中心调整错误地带动基座接口，或未移动铜排。");
                changed["parameters"] = parameters.DeepClone();
                changed["parameters"]["Cabinet"]["FrontHole"]["BaseToCabinetEdgeDistance"] =
                    (double)parameters["Cabinet"]["FrontHole"]["BaseToCabinetEdgeDistance"] + 20;
                var movedBase = (await calculator.ComputeAsync(changed))["upstream"];
                if (JToken.DeepEquals(baseline["opening"], movedBase["sides"][0]["opening"]) ||
                    !JToken.DeepEquals(baseline["extensions"][0]["interfacePoint"], movedBase["sides"][0]["extensions"][0]["interfacePoint"]))
                    throw new InvalidOperationException("基座定位调整错误地带动铜排，或未移动基座接口。");
                Console.WriteLine("柜前向墙基座定位与铜排中心独立联动核查通过。");
            }
        }
    }
}
