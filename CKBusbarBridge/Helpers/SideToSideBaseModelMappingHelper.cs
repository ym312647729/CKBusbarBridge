using CKBusbarBridge.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 构建侧对侧基座的“模型对照映射”JSON，键结构与现有独立基座读取约定一致。
    /// </summary>
    internal static class SideToSideBaseModelMappingHelper
    {
        internal const string PropertyName = "模型对照映射";

        internal static readonly string[] OrderedPositions =
        {
            "A柜前", "A柜后", "B柜前", "B柜后"
        };

        internal static string BuildJson(
            IEnumerable<SideToSideBaseAssemblyToken> tokens)
        {
            if (tokens == null) throw new ArgumentNullException(nameof(tokens));

            var entries = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                if (token == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(token.ComponentFullName))
                {
                    throw new InvalidOperationException(
                        token.PositionName + "缺少组件全名，无法写入模型对照映射。");
                }

                if (!entries.TryGetValue(token.PositionName, out var entry))
                {
                    entry = new JObject
                    {
                        ["普通基座"] = null,
                        ["增高基座"] = null
                    };
                    entries[token.PositionName] = entry;
                }

                // 基座1和基座2都属于后续读取口径中的“普通基座”。
                entry[token.Kind == SideToSideBaseModelKind.ElevatedBase
                    ? "增高基座"
                    : "普通基座"] = token.ComponentFullName;
            }

            var bases = new JObject();
            foreach (var position in OrderedPositions)
            {
                if (entries.TryGetValue(position, out var entry))
                {
                    bases[position] = entry;
                }
            }

            return new JObject { ["基座"] = bases }.ToString(Formatting.None);
        }

        internal static bool TryGetComponentFullName(
            string mappingJson,
            string positionName,
            SideToSideBaseModelKind kind,
            out string componentFullName)
        {
            componentFullName = null;
            if (string.IsNullOrWhiteSpace(mappingJson)
                || string.IsNullOrWhiteSpace(positionName))
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(mappingJson);
                var key = kind == SideToSideBaseModelKind.ElevatedBase
                    ? "增高基座"
                    : "普通基座";
                componentFullName = root["基座"]?[positionName]?[key]?.Value<string>();
                return !string.IsNullOrWhiteSpace(componentFullName);
            }
            catch (JsonException)
            {
                componentFullName = null;
                return false;
            }
        }
    }
}
