using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // One Chinese property-name contract is shared by export, restore and readback.
    internal static class CabinetParameterPropertyCodec
    {
        internal const string VersionProperty = "母线桥参数草稿版本";
        internal const string Version = "cabinet-inputs/2";
        private static readonly JObject Contracts = LoadContracts();
        private static readonly Dictionary<string, string> Scenarios = new Dictionary<string, string>
            { ["face-to-face"] = "面对面", ["side-to-side"] = "侧对侧", ["face-to-wall"] = "面对墙" };
        private static readonly string[] RequiredDrawingProperties = { "合同号", "图号" };
        internal static readonly IReadOnlyList<string> OptionalPersonnelProperties = Array.AsReadOnly(new[] { "设计", "校对", "审核", "批准" });
        private static JObject LoadContracts()
        {
            using (var stream = typeof(CabinetParameterPropertyCodec).Assembly.GetManifestResourceStream("CKBusbarBridge.CabinetParameterProperties.json"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("缺少柜体输入属性名称表。")))
            {
                var contracts = JObject.Parse(reader.ReadToEnd());
                foreach (var scenario in contracts.Properties())
                {
                    var fields = ((JArray)scenario.Value).OfType<JObject>().ToArray();
                    foreach (var field in fields)
                    {
                        var name = (string)field["name"];
                        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '{', '}' }) >= 0 ||
                            name.IndexOf("Binding", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw Invalid(name, "属性名称表必须使用实际中文标签");
                    }
                    if (fields.GroupBy(field => (string)field["name"], StringComparer.Ordinal).Any(group => group.Count() > 1))
                        throw new InvalidOperationException("柜体属性名称表包含重复名称：" + scenario.Name);
                }
                return contracts;
            }
        }
        internal static IReadOnlyList<JObject> Fields(string scenario) => (Contracts[scenario] as JArray)?.OfType<JObject>().ToArray()
            ?? throw new InvalidOperationException("设计场景属性不支持：" + scenario);
        internal static IReadOnlyDictionary<string, string> DrawingInputs(string drawingMetadata)
        {
            var metadata = JObject.Parse(drawingMetadata ?? throw Invalid("图号", "缺少图号信息"));
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in RequiredDrawingProperties)
            {
                var value = metadata[name]?.Value<string>();
                if (string.IsNullOrWhiteSpace(value)) throw Invalid(name, "请在图号生成窗口补填并确认");
                values.Add(name, value.Trim());
            }
            foreach (var name in OptionalPersonnelProperties) values.Add(name, metadata[name]?.Value<string>()?.Trim() ?? string.Empty);
            CKBusbarBridge.DesignPreview.CabinetDraftSavePath.ValidateDrawingNumber(values["图号"]);
            return values;
        }
        internal static Dictionary<string, string> Encode(string scenario, JObject parameters, string orientation, string drawingMetadata)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal) { [VersionProperty] = Version, ["设计场景"] = Scenarios[scenario],
                ["柜体朝向"] = orientation ?? "" };
            if (scenario == "face-to-wall" && orientation != "CabinetFrontToWall" && orientation != "CabinetRearToWall")
                throw Invalid("柜体朝向", "仅支持柜前向墙或柜后向墙");
            foreach (var value in DrawingInputs(drawingMetadata))
                if (value.Value.Length != 0) result[value.Key] = value.Value;
            foreach (var field in Fields(scenario))
            {
                var name = (string)field["name"]; var token = parameters.SelectToken((string)field["path"]);
                if (token == null) throw Invalid(name, "输入字段缺失");
                if (token.Type == JTokenType.Null)
                { if ((bool)field["nullable"]) { result[name] = "null"; continue; } throw Invalid(name, "不允许空值"); }
                switch ((string)field["kind"])
                {
                    case "number": result[name] = SinglePrecision.Number(token).ToString("R", CultureInfo.InvariantCulture); break;
                    case "integer": result[name] = token.Value<int>().ToString(CultureInfo.InvariantCulture); break;
                    case "boolean": result[name] = token.Value<bool>() ? "true" : "false"; break;
                    default: result[name] = token.Value<string>(); break;
                }
            }
            return result;
        }
        internal static CabinetPropertySnapshot Decode(Func<string, string> read)
        {
            string Required(string name) => read(name) ?? throw Invalid(name, "必需属性缺失");
            if (Required(VersionProperty) != Version) throw Invalid(VersionProperty, "不支持此参数结构版本");
            var scene = Required("设计场景"); var scenario = Scenarios.FirstOrDefault(pair => pair.Value == scene).Key;
            if (scenario == null) throw Invalid("设计场景", "不支持的场景：" + scene);
            var orientation = Required("柜体朝向");
            if (scenario == "face-to-wall" && orientation != "CabinetFrontToWall" && orientation != "CabinetRearToWall") throw Invalid("柜体朝向", "朝向值无法解析");
            if (scenario != "face-to-wall" && orientation.Length != 0) throw Invalid("柜体朝向", "此场景不应包含面对墙朝向");
            var parameters = new JObject(); var metadata = new JObject();
            foreach (var name in RequiredDrawingProperties) metadata[name] = Required(name);
            foreach (var name in OptionalPersonnelProperties) metadata[name] = read(name) ?? string.Empty;
            DrawingInputs(metadata.ToString(Newtonsoft.Json.Formatting.None));
            foreach (var field in Fields(scenario))
            {
                var name = (string)field["name"]; var raw = Required(name); JToken token;
                if (raw == "null" && (bool)field["nullable"]) token = JValue.CreateNull();
                else switch ((string)field["kind"])
                {
                    case "number":
                        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || float.IsNaN(number) || float.IsInfinity(number)) throw Invalid(name, "无法解析为有限数值");
                        token = new JValue(number); break;
                    case "integer":
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) throw Invalid(name, "无法解析为整数");
                        token = new JValue(count); break;
                    case "boolean":
                        if (!bool.TryParse(raw, out var enabled)) throw Invalid(name, "无法解析为开关值");
                        token = new JValue(enabled); break;
                    default: token = new JValue(raw); break;
                }
                var segments = ((string)field["path"]).Split('.'); var parent = parameters;
                foreach (var segment in segments.Take(segments.Length - 1))
                { if (!(parent[segment] is JObject child)) parent[segment] = child = new JObject(); parent = child; }
                parent[segments.Last()] = token;
            }
            // Required inputs were read first. Model constructors may now derive read-only values.
            var restored = BridgeCabinetParameterBinding.Restore(scenario, parameters);
            var snapshot = BridgeParameterSnapshotBinding.Capture(restored);
            foreach (var field in Fields(scenario))
            {
                var before = parameters.SelectToken((string)field["path"]); var after = snapshot.SelectToken((string)field["path"]);
                var equal = before?.Type == JTokenType.Null ? after?.Type == JTokenType.Null : (string)field["kind"] == "number"
                    ? after != null && Math.Abs(SinglePrecision.Number(before) - SinglePrecision.Number(after)) <= 1.0f : JToken.DeepEquals(before, after);
                if (!equal) throw Invalid((string)field["name"], "原参数规则未接受保存值");
            }
            return new CabinetPropertySnapshot { Scenario = scenario, Parameters = snapshot, Orientation = orientation.Length == 0 ? null : orientation,
                DrawingMetadata = metadata.ToString(Newtonsoft.Json.Formatting.None) };
        }
        private static InvalidOperationException Invalid(string name, string detail) => new InvalidOperationException("柜体草稿属性“" + name + "”：" + detail + "。");
    }
    internal sealed class CabinetPropertySnapshot
    {
        internal string Scenario, Orientation, DrawingMetadata;
        internal JObject Parameters;
    }
}
