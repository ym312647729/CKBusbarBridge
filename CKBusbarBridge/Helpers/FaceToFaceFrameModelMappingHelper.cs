using CKBusbarBridge.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 维护面对面基架与既有基座共用的「模型对照映射」属性。
    /// </summary>
    internal static class FaceToFaceFrameModelMappingHelper
    {
        private const string BasesRootKey = "基座";
        private const string FramesRootKey = "基架";
        private const string OrdinaryBaseKey = "普通基座";
        private const string NameKey = "名称";
        private const string ComponentFullNameKey = "零部件全名";
        private const string TemplateIdentityKey = "模板身份";

        internal static bool TryGetOrdinaryBaseComponentFullName(
            string existingJson,
            string positionName,
            out string fullName)
        {
            fullName = null;
            if (string.IsNullOrWhiteSpace(existingJson)
                || string.IsNullOrWhiteSpace(positionName))
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(existingJson);
                var entry = root[BasesRootKey]?[positionName] as JObject;
                fullName = entry?[OrdinaryBaseKey]?.Value<string>();
                return !string.IsNullOrWhiteSpace(fullName);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("既有模型对照映射不是有效 JSON。", nameof(existingJson), exception);
            }
        }

        /// <summary>
        /// 严格读取映射中“组件全名→模板身份”；供物理模型属性冲突校验使用。
        /// 旧映射未写身份时跳过该项，不据 024 文件名前缀猜测。
        /// </summary>
        internal static IReadOnlyDictionary<string, FrameTemplateIdentity>
            ReadFrameTemplateIdentities(string existingJson)
        {
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                return new Dictionary<string, FrameTemplateIdentity>(
                    StringComparer.OrdinalIgnoreCase);
            }

            JObject root;
            try
            {
                root = JObject.Parse(existingJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "既有模型对照映射不是有效 JSON。",
                    nameof(existingJson),
                    exception);
            }

            var frames = root[FramesRootKey] as JObject;
            if (frames == null)
            {
                if (root[FramesRootKey] != null)
                {
                    throw new ArgumentException(
                        "既有模型对照映射中的基架节点必须是对象。",
                        nameof(existingJson));
                }

                return new Dictionary<string, FrameTemplateIdentity>(
                    StringComparer.OrdinalIgnoreCase);
            }

            var identities = new Dictionary<string, FrameTemplateIdentity>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var position in frames.Properties())
            {
                var entry = position.Value as JObject;
                if (entry == null)
                {
                    throw new ArgumentException(
                        "模型对照映射中的基架位置必须是对象：" + position.Name + "。",
                        nameof(existingJson));
                }

                var componentFullName = entry[ComponentFullNameKey]?.Value<string>();
                var identityText = entry[TemplateIdentityKey]?.Value<string>();
                if (string.IsNullOrWhiteSpace(componentFullName)
                    || string.IsNullOrWhiteSpace(identityText))
                {
                    continue;
                }

                if (!FrameTemplateIdentityMetadata.TryParse(
                    identityText,
                    out var identity))
                {
                    throw new ArgumentException(
                        "模型对照映射中的基架模板身份无效：位置="
                        + position.Name + "；值=" + identityText + "。",
                        nameof(existingJson));
                }

                if (identities.TryGetValue(componentFullName, out var existingIdentity)
                    && existingIdentity != identity)
                {
                    throw new ArgumentException(
                        "同一基架组件在模型对照映射中存在冲突模板身份："
                        + componentFullName + "。",
                        nameof(existingJson));
                }

                identities[componentFullName.Trim()] = identity;
            }

            return identities;
        }

        internal static string MergeFrames(
            string existingJson,
            IEnumerable<FaceToFaceFrameAssemblyToken> tokens)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }

            JObject root;
            try
            {
                root = string.IsNullOrWhiteSpace(existingJson)
                    ? new JObject()
                    : JObject.Parse(existingJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("既有模型对照映射不是有效 JSON。", nameof(existingJson), exception);
            }

            var frames = root[FramesRootKey] as JObject;
            if (frames == null)
            {
                if (root[FramesRootKey] != null)
                {
                    throw new ArgumentException("既有模型对照映射中的基架节点必须是对象。", nameof(existingJson));
                }

                frames = new JObject();
                root[FramesRootKey] = frames;
            }

            foreach (var token in tokens.OrderBy(item => item?.PositionPlan?.PositionIndex))
            {
                if (token == null || token.PositionPlan == null)
                {
                    throw new ArgumentException("基架装配记录不能为空且必须包含位置计划。", nameof(tokens));
                }

                if (string.IsNullOrWhiteSpace(token.ComponentFullName))
                {
                    throw new InvalidOperationException(
                        $"{token.PositionPlan.PositionKey}缺少基架零部件全名，无法写入模型对照映射。");
                }

                frames[token.PositionPlan.PositionKey] = new JObject
                {
                    [NameKey] = token.PositionPlan.SourceName,
                    [ComponentFullNameKey] = token.ComponentFullName,
                    [TemplateIdentityKey] = FrameTemplateIdentityMetadata.Serialize(
                        token.PositionPlan.ModelPlan.TemplateIdentity)
                };
            }

            var orderedFrames = new JObject();
            foreach (var property in frames.Properties()
                .OrderBy(property => TryGetPositionIndex(property.Name, out _) ? 0 : 1)
                .ThenBy(property => TryGetPositionIndex(property.Name, out var index)
                    ? index
                    : int.MaxValue)
                .ThenBy(property => property.Name, StringComparer.Ordinal))
            {
                orderedFrames.Add(property.Name, property.Value.DeepClone());
            }

            root[FramesRootKey] = orderedFrames;

            return root.ToString(Formatting.None);
        }

        internal static string ReplaceFrames(
            string existingJson,
            IEnumerable<FaceToFaceFrameAssemblyToken> tokens)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }

            JObject root;
            try
            {
                root = string.IsNullOrWhiteSpace(existingJson)
                    ? new JObject()
                    : JObject.Parse(existingJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("既有模型对照映射不是有效 JSON。", nameof(existingJson), exception);
            }

            if (root[FramesRootKey] != null && !(root[FramesRootKey] is JObject))
            {
                throw new ArgumentException("既有模型对照映射中的基架节点必须是对象。", nameof(existingJson));
            }

            var frames = new JObject();
            foreach (var token in tokens.OrderBy(item => item?.PositionPlan?.PositionIndex))
            {
                if (token == null || token.PositionPlan == null)
                {
                    throw new ArgumentException("基架装配记录不能为空且必须包含位置计划。", nameof(tokens));
                }

                if (string.IsNullOrWhiteSpace(token.ComponentFullName))
                {
                    throw new InvalidOperationException(
                        $"{token.PositionPlan.PositionKey}缺少基架零部件全名，无法写入模型对照映射。");
                }

                frames[token.PositionPlan.PositionKey] = new JObject
                {
                    [NameKey] = token.PositionPlan.SourceName,
                    [ComponentFullNameKey] = token.ComponentFullName,
                    [TemplateIdentityKey] = FrameTemplateIdentityMetadata.Serialize(
                        token.PositionPlan.ModelPlan.TemplateIdentity)
                };
            }

            // 二次生成以最终链为准整体替换节点，避免保留已删除位置的旧映射。
            root[FramesRootKey] = frames;
            return root.ToString(Formatting.None);
        }

        private static bool TryGetPositionIndex(string key, out int positionIndex)
        {
            positionIndex = 0;
            if (string.IsNullOrWhiteSpace(key)
                || !key.StartsWith("位置", StringComparison.Ordinal))
            {
                return false;
            }

            return int.TryParse(key.Substring("位置".Length), out positionIndex)
                && positionIndex >= 0;
        }
    }
}
