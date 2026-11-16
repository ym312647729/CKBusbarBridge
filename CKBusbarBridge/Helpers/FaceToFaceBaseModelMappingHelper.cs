using CKBusbarBridge.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 面对面独立基座「模型对照映射」自定义属性的构建与解析。
    /// JSON 中保存的是总装内零部件全名（含实例号，来自 Component2.Name2）。
    /// </summary>
    internal static class FaceToFaceBaseModelMappingHelper
    {
        /// <summary>
        /// 写入总装配体的自定义属性名称。
        /// </summary>
        internal const string PropertyName = "模型对照映射";

        private const string RootKey = "基座";
        private const string OrdinaryBaseKey = "普通基座";
        private const string ElevatedBaseKey = "增高基座";

        /// <summary>
        /// 面对面总装固定四个位置键，未生成的位置保留 null，便于人工核对。
        /// </summary>
        internal static readonly string[] FaceToFaceOrderedPositions =
        {
            "A柜前", "A柜后", "B柜前", "B柜后"
        };

        /// <summary>
        /// 面对墙总装只有柜前、柜后两个位置键。
        /// </summary>
        internal static readonly string[] FaceToWallOrderedPositions =
        {
            "柜前", "柜后"
        };

        /// <summary>
        /// 根据本次已插入的基座令牌构建映射 JSON。
        /// 同一位置可同时包含普通基座与增高基座；无增高时对应值为 null。
        /// </summary>
        /// <param name="orderedPositions">输出键顺序；为空时按面对面四位置输出。</param>
        internal static string BuildJson(
            IEnumerable<FaceToFaceBaseAssemblyToken> insertedTokens,
            IReadOnlyList<string> orderedPositions = null)
        {
            if (insertedTokens == null)
            {
                throw new ArgumentNullException(nameof(insertedTokens));
            }

            // 仅记录基座组件；按位置聚合普通/增高全名。
            var positionMap = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var token in insertedTokens)
            {
                if (token == null || token.IsInsulator)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(token.ComponentFullName))
                {
                    throw new InvalidOperationException(
                        $"{token.PositionName}{GetKindDisplayName(token.Kind)}缺少零部件全名，无法写入模型对照映射。");
                }

                JObject entry;
                if (!positionMap.TryGetValue(token.PositionName, out entry))
                {
                    entry = new JObject
                    {
                        [OrdinaryBaseKey] = null,
                        [ElevatedBaseKey] = null
                    };
                    positionMap[token.PositionName] = entry;
                }

                if (token.Kind == FaceToFaceBaseModelKind.ElevatedBase)
                {
                    entry[ElevatedBaseKey] = token.ComponentFullName;
                }
                else
                {
                    entry[OrdinaryBaseKey] = token.ComponentFullName;
                }
            }

            // 按调用方给定的位置顺序输出；未生成的位置保留 null。
            var positionOrder = orderedPositions == null || orderedPositions.Count == 0
                ? FaceToFaceOrderedPositions
                : orderedPositions;
            var basesObject = new JObject();
            foreach (var positionName in positionOrder)
            {
                JObject entry;
                if (positionMap.TryGetValue(positionName, out entry))
                {
                    basesObject[positionName] = entry;
                }
            }

            // 若还有其它位置名（防御性），追加到末尾。
            foreach (var pair in positionMap)
            {
                if (basesObject[pair.Key] == null)
                {
                    basesObject[pair.Key] = pair.Value;
                }
            }

            var root = new JObject
            {
                [RootKey] = basesObject
            };
            return root.ToString(Formatting.None);
        }

        /// <summary>
        /// 尝试解析映射 JSON，并取出指定位置普通/增高基座的零部件全名。
        /// </summary>
        internal static bool TryGetComponentFullName(
            string mappingJson,
            string positionName,
            bool elevated,
            out string componentFullName)
        {
            componentFullName = null;
            if (string.IsNullOrWhiteSpace(mappingJson) || string.IsNullOrWhiteSpace(positionName))
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(mappingJson);
                var bases = root[RootKey] as JObject;
                if (bases == null)
                {
                    return false;
                }

                var entry = bases[positionName] as JObject;
                if (entry == null)
                {
                    return false;
                }

                var token = entry[elevated ? ElevatedBaseKey : OrdinaryBaseKey];
                if (token == null || token.Type == JTokenType.Null)
                {
                    return false;
                }

                componentFullName = token.Value<string>();
                return !string.IsNullOrWhiteSpace(componentFullName);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>按保存的角色映射核对启用的顶层基座，不用 RD类型 区分普通与增高基座。</summary>
        internal static IReadOnlyList<FaceToFaceBaseInstanceMapping> ResolveFaceToFaceBases(
            string mappingJson, IEnumerable<string> enabledTopLevelBaseNames)
        {
            if (enabledTopLevelBaseNames == null)
                throw new ArgumentNullException(nameof(enabledTopLevelBaseNames));

            var candidates = enabledTopLevelBaseNames.ToArray();
            var result = new List<FaceToFaceBaseInstanceMapping>();
            var mappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var side in new[] { "A", "B" })
            {
                var positions = new[] { side + "柜前", side + "柜后" };
                var activePositions = positions.Where(position =>
                    TryGetComponentFullName(mappingJson, position, false, out var ignored)).ToArray();
                if (activePositions.Length != 1)
                    throw new InvalidOperationException(side + "柜必须唯一映射一个启用普通基座，不能猜测柜前/柜后归属。");

                var positionName = activePositions[0];
                TryGetComponentFullName(mappingJson, positionName, false, out var ordinary);
                TryGetComponentFullName(mappingJson, positionName, true, out var elevated);
                foreach (var inactivePosition in positions.Where(position => position != positionName))
                {
                    if (TryGetComponentFullName(mappingJson, inactivePosition, true, out var ignored))
                        throw new InvalidOperationException(inactivePosition + "映射了增高基座但缺少普通基座。");
                }

                foreach (var name in new[] { ordinary, elevated }.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!mappedNames.Add(StripSelectPathSuffix(name)))
                        throw new InvalidOperationException("普通/增高基座模型对照映射重复指向同一实例：" + name);
                    if (candidates.Count(candidate => ComponentFullNameEquals(candidate, name)) != 1)
                        throw new InvalidOperationException("模型对照映射必须指向唯一启用的顶层基座：" + positionName + "/" + name);
                }
                result.Add(new FaceToFaceBaseInstanceMapping(positionName, ordinary, elevated));
            }

            // 普通与增高模板都可能声明 RD类型=基座；所有启用实例必须由映射中的具体角色解释。
            var unmapped = candidates.Where(name => !mappedNames.Contains(StripSelectPathSuffix(name))).ToArray();
            if (unmapped.Length != 0)
                throw new InvalidOperationException("存在未登记的启用顶层基座，请核对模型对照映射：" + string.Join("、", unmapped));
            return result;
        }

        /// <summary>
        /// 二次拆模后只更新单个位置的零部件全名，保留其余位置映射。
        /// </summary>
        internal static string ReplaceComponentFullName(
            string mappingJson,
            string positionName,
            bool elevated,
            string newComponentFullName)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射 JSON 不能为空。", nameof(mappingJson));
            }

            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("位置名称不能为空。", nameof(positionName));
            }

            if (string.IsNullOrWhiteSpace(newComponentFullName))
            {
                throw new ArgumentException("新的零部件全名不能为空。", nameof(newComponentFullName));
            }

            var root = JObject.Parse(mappingJson);
            var bases = root[RootKey] as JObject;
            if (bases == null)
            {
                throw new InvalidOperationException("模型对照映射缺少基座节点。");
            }

            var entry = bases[positionName] as JObject;
            if (entry == null)
            {
                entry = new JObject
                {
                    [OrdinaryBaseKey] = null,
                    [ElevatedBaseKey] = null
                };
                bases[positionName] = entry;
            }

            entry[elevated ? ElevatedBaseKey : OrdinaryBaseKey] = newComponentFullName;
            return root.ToString(Formatting.None);
        }

        /// <summary>
        /// 去掉增高后将该位置的增高基座映射置为 null，保留普通基座全名。
        /// </summary>
        internal static string ClearComponentFullName(
            string mappingJson,
            string positionName,
            bool elevated)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射 JSON 不能为空。", nameof(mappingJson));
            }

            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("位置名称不能为空。", nameof(positionName));
            }

            var root = JObject.Parse(mappingJson);
            var bases = root[RootKey] as JObject;
            if (bases == null)
            {
                throw new InvalidOperationException("模型对照映射缺少基座节点。");
            }

            var entry = bases[positionName] as JObject;
            if (entry == null)
            {
                return mappingJson;
            }

            entry[elevated ? ElevatedBaseKey : OrdinaryBaseKey] = null;
            return root.ToString(Formatting.None);
        }

        /// <summary>
        /// 比较零部件全名是否一致。保留实例号，只去掉选择路径中的 @ 后缀。
        /// </summary>
        internal static bool ComponentFullNameEquals(string left, string right)
        {
            return string.Equals(
                StripSelectPathSuffix(left),
                StripSelectPathSuffix(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string StripSelectPathSuffix(string componentFullName)
        {
            if (string.IsNullOrWhiteSpace(componentFullName))
            {
                return string.Empty;
            }

            var normalized = componentFullName.Trim();
            var atIndex = normalized.IndexOf('@');
            if (atIndex >= 0)
            {
                normalized = normalized.Substring(0, atIndex);
            }

            return normalized.Trim();
        }

        private static string GetKindDisplayName(FaceToFaceBaseModelKind kind)
        {
            return kind == FaceToFaceBaseModelKind.ElevatedBase ? "增高基座" : "普通基座";
        }
    }

    internal sealed class FaceToFaceBaseInstanceMapping
    {
        internal FaceToFaceBaseInstanceMapping(string positionName, string ordinaryComponentFullName,
            string elevatedComponentFullName)
        {
            PositionName = positionName;
            OrdinaryComponentFullName = ordinaryComponentFullName;
            ElevatedComponentFullName = elevatedComponentFullName;
        }

        internal string PositionName { get; }
        internal string OrdinaryComponentFullName { get; }
        internal string ElevatedComponentFullName { get; }
    }
}
