using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RouteLinkBar.Algorithm.Transport;

namespace RouteLinkBar.Algorithm.Worker
{
    /// <summary>仅为本项目独立包补齐旧请求没有的字段；显式值、几何与搜索预算均由宿主决定。</summary>
    internal sealed class CompatibilityInputReader : TextReader
    {
        private readonly TextReader sourceReader;
        private readonly JObject defaultRequest;
        private readonly JObject defaultSearchOptions;

        internal CompatibilityInputReader(TextReader sourceReader)
        {
            this.sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
            defaultRequest = JObject.FromObject(new LinkageRouteWorkerRequestDto());
            // 旧母线桥没有方案库入口；不为一个缺失的开关打开数据库业务。
            defaultRequest[nameof(LinkageRouteWorkerRequestDto.EnableSchemeLibraryMatching)] = false;
            // 母线桥的既有默认：只生成XYZ轴向路径。
            defaultRequest[nameof(LinkageRouteWorkerRequestDto.RequireAxisAlignedRoutes)] = true;
            defaultSearchOptions = JObject.FromObject(new LinkageRouteWorkerSearchOptionsDto());
        }

        public override string ReadLine()
        {
            var sourceLine = sourceReader.ReadLine();
            return sourceLine == null ? null : Normalize(sourceLine);
        }

        internal string Normalize(string sourceLine)
        {
            try
            {
                JObject command;
                using (var jsonReader = new JsonTextReader(new StringReader(sourceLine.TrimStart('\uFEFF'))))
                {
                    jsonReader.DateParseHandling = DateParseHandling.None;
                    jsonReader.FloatParseHandling = FloatParseHandling.Double;
                    command = JObject.Load(jsonReader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (jsonReader.Read()) return sourceLine; // 多个顶层值交给原协议拒绝。
                }
                if (command["MessageType"]?.Type != JTokenType.String ||
                    !string.Equals((string)command["MessageType"], LinkageRouteWorkerProtocol.SolveRequest,
                        StringComparison.Ordinal) || !(command["Request"] is JObject request)) return sourceLine;
                AddMissing(request, defaultRequest);
                if (request["SearchOptions"] is JObject searchOptions) AddMissing(searchOptions, defaultSearchOptions);
                return command.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                // 原Worker负责无效协议诊断，不能把无效输入转换成一个默认可求解请求。
                return sourceLine;
            }
        }

        private static void AddMissing(JObject target, JObject defaults)
        {
            foreach (var property in defaults.Properties())
                if (target.Property(property.Name, StringComparison.Ordinal) == null)
                    target.Add(property.Name, property.Value.DeepClone());
        }
    }
}
