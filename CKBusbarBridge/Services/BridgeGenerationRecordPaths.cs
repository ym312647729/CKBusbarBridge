using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // 路径只在持久化边界转换；模型核验与事务代码始终使用当前总装下的完整路径。
    internal sealed class BridgeGenerationRecordPaths
    {
        internal const int RelativeSchema = 6;
        private readonly string _assembly;
        private readonly string _project;

        internal BridgeGenerationRecordPaths(string assembly)
        {
            if (string.IsNullOrWhiteSpace(assembly) || !Path.IsPathRooted(assembly))
                throw new InvalidOperationException("总装尚无完整保存路径，无法确定项目目录。");
            _assembly = Path.GetFullPath(assembly);
            _project = Path.GetDirectoryName(_assembly);
        }

        internal string RelativePath(string path) => RelativeTo(_project, path);

        internal string LegacyPath(string path, string legacyAssembly)
            => Path.Combine(Path.GetDirectoryName(legacyAssembly), RelativePath(path));

        internal string WriteRecord(string json)
        {
            var data = Parse(json);
            MapRecord(data, RelativePath, _assembly, string.Empty);
            data["Schema"] = RelativeSchema;
            data.Remove("Assembly");
            data.Remove("RecordDigest");
            return data.ToString(Formatting.None);
        }

        // 调用者必须先验证原始文件摘要/生成编号，不能先迁移再为未知内容重算摘要。
        internal string ReadRecord(string verifiedJson, out string legacyAssembly)
        {
            var data = Parse(verifiedJson);
            var relative = data.Value<int>("Schema") == RelativeSchema;
            legacyAssembly = relative ? null : RequireLegacyAssembly(data);
            if (relative && data["Assembly"] != null)
                throw new InvalidOperationException("相对路径生成记录不应保存总装绝对路径。");
            MapRecord(data, Reader(relative, legacyAssembly), relative ? string.Empty : legacyAssembly, _assembly);
            data["Assembly"] = _assembly;
            return data.ToString(Formatting.None);
        }

        internal string WritePending(string json)
        {
            var data = Parse(json);
            var effective = data["EffectiveRecord"] as JObject;
            if (effective == null) throw new InvalidOperationException("待同步记录缺少正式基线。");
            MapRecord(effective, RelativePath, _assembly, string.Empty);
            effective.Remove("Assembly");
            MapPending(data, RelativePath, _assembly, string.Empty);
            data["PathFormat"] = 1;
            if (data.Value<int>("Schema") >= 2 && string.IsNullOrWhiteSpace(effective.Value<string>("GenerationId")))
                data["RecordHash"] = effective["Fingerprint"];
            return data.ToString(Formatting.Indented);
        }

        internal string ReadPending(string verifiedJson, string verifiedOriginal, string currentFingerprint)
        {
            var data = Parse(verifiedJson);
            var original = Parse(verifiedOriginal);
            var effective = data["EffectiveRecord"] as JObject;
            if (effective == null) throw new InvalidOperationException("待同步记录缺少正式基线。");
            var format = data.Value<int>("PathFormat");
            if (format != 0 && format != 1) throw new InvalidOperationException("待同步记录路径版本不受支持。");
            var relative = format == 1;
            var legacyAssembly = relative ? null : RequireLegacyAssembly(original);
            if ((!relative && effective.Value<string>("Assembly") != legacyAssembly) ||
                (relative && effective["Assembly"] != null))
                throw new InvalidOperationException("待同步记录与正式基线的总装身份不匹配。");
            var originalRecipeHash = effective.Value<string>("Fingerprint");
            var map = Reader(relative, legacyAssembly);
            MapRecord(effective, map, relative ? string.Empty : legacyAssembly, _assembly);
            effective["Assembly"] = _assembly;
            MapPending(data, map, relative ? string.Empty : legacyAssembly, _assembly);
            // 旧版无生成编号的待同步记录按方案摘要关联；只转换原本已匹配的摘要。
            if (data.Value<int>("Schema") >= 2 && string.IsNullOrWhiteSpace(original.Value<string>("GenerationId")) &&
                data.Value<string>("RecordHash") == originalRecipeHash)
                data["RecordHash"] = currentFingerprint;
            data.Remove("PathFormat");
            return data.ToString(Formatting.None);
        }

        private Func<string, string> Reader(bool relative, string legacyAssembly)
        {
            if (relative) return AbsolutePath;
            var oldProject = Path.GetDirectoryName(legacyAssembly);
            return path => AbsolutePath(RelativeTo(oldProject, path));
        }

        private string RequireLegacyAssembly(JObject data)
        {
            var assembly = data.Value<string>("Assembly");
            if (string.IsNullOrWhiteSpace(assembly) || !Path.IsPathRooted(assembly))
                throw new InvalidOperationException("旧生成记录缺少原总装路径，不能确定引用文件的相对位置。");
            return Path.GetFullPath(assembly);
        }

        private string RelativeTo(string project, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (!Path.IsPathRooted(path)) throw new InvalidOperationException("生成记录引用缺少完整路径：" + path);
            var prefix = Path.GetFullPath(project).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("生成记录引用超出项目目录：" + path);
            return full.Substring(prefix.Length);
        }

        private string AbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (Path.IsPathRooted(path)) throw new InvalidOperationException("相对路径生成记录包含绝对引用：" + path);
            var full = Path.GetFullPath(Path.Combine(_project, path));
            RelativePath(full); // 相对引用也必须留在当前项目内，不能越界绑定其他工程。
            return full;
        }

        private void MapRecord(JObject data, Func<string, string> map, string oldAssembly, string newAssembly)
        {
            var recipe = data.Value<string>("Recipe");
            var codec = new BridgeGenerationRecipeCodec();
            if (string.IsNullOrWhiteSpace(recipe) || codec.Hash(recipe) != data.Value<string>("Fingerprint"))
                throw new InvalidOperationException("正式生成记录的业务方案校验不符。");
            var recipeData = Parse(recipe);
            MapBindings(recipeData, map);
            var updated = recipeData.ToString(Formatting.None);
            if (updated != recipe)
            {
                var copper = data.Value<string>("CopperFingerprint");
                if (!string.IsNullOrWhiteSpace(copper))
                {
                    if (codec.CopperFingerprint(recipe, data.Value<string>("Material")) != copper)
                        throw new InvalidOperationException("正式生成记录的铜排分项指纹不符。");
                    data["CopperFingerprint"] = codec.CopperFingerprint(updated, data.Value<string>("Material"));
                }
                data["Recipe"] = updated;
                data["Fingerprint"] = codec.Hash(updated);
            }
            MapPath(data, "CopperPath", map);
            MapKeys(data["Files"] as JObject, map);
            foreach (var field in new[] { "CopperParts", "Hangers" })
                foreach (var item in (data[field] as JObject)?.Properties().ToArray() ?? Array.Empty<JProperty>())
                    MapPath(item.Value as JObject, "Path", map);
            MapState(data["ModelState"] as JObject, map);
            MapUpstream(data, "Upstream", oldAssembly, newAssembly);
        }

        private void MapPending(JObject data, Func<string, string> map, string oldAssembly, string newAssembly)
        {
            var files = data["CabinetFiles"] as JArray;
            if (files != null)
                for (var i = 0; i < files.Count; i++) files[i] = map(files[i].Value<string>());
            MapState(data["BaselineState"] as JObject, map);
            foreach (var binding in data["FrameBindings"] as JArray ?? new JArray())
                MapPath(binding as JObject, "Path", map);
            foreach (var change in data["InstallationChanges"] as JArray ?? new JArray())
            {
                foreach (var state in new[] { change["Before"] as JObject, change["After"] as JObject })
                {
                    MapPath(state?["Ordinary"] as JObject, "Path", map);
                    MapPath(state?["Elevated"] as JObject, "Path", map);
                }
                MapPath(change["FrameAnchor"] as JObject, "Path", map);
            }
            MapUpstream(data, "TargetUpstream", oldAssembly, newAssembly);
        }

        private void MapState(JObject state, Func<string, string> map)
        {
            if (state == null) return;
            MapKeys(state["FileHashes"] as JObject, map);
            var documents = state["Documents"] as JObject;
            if (documents != null)
            {
                foreach (var item in documents.Properties()) MapPath(item.Value as JObject, "Path", map);
                MapKeys(documents, path => MapDocumentKey(path, map));
            }
            foreach (var item in (state["Instances"] as JObject)?.Properties().ToArray() ?? Array.Empty<JProperty>())
                MapPath(item.Value as JObject, "Document", path => MapDocumentKey(path, map));
            foreach (var installation in (state["Installations"] as JObject)?.Properties().ToArray() ?? Array.Empty<JProperty>())
            {
                MapPath(installation.Value["Ordinary"] as JObject, "Path", map);
                MapPath(installation.Value["Elevated"] as JObject, "Path", map);
            }
            // Connections.Path 是“总装配合/名称”的业务标识；模型原始属性、方程式和几何也不得改写。
        }

        private void MapBindings(JToken token, Func<string, string> map)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                if (obj["Binding"] is JObject binding)
                {
                    MapPath(binding, "Path", map);
                    MapPath(binding, "DocumentKey", path => MapDocumentKey(path, map));
                }
                foreach (var property in obj.Properties())
                {
                    if (property.Name == "Binding") continue;
                    if (property.Name == "WallPlanning" && property.Value.Type == JTokenType.String)
                    {
                        var embedded = Parse(property.Value.Value<string>());
                        MapBindings(embedded, map);
                        property.Value = embedded.ToString(Formatting.None);
                    }
                    else MapBindings(property.Value, map);
                }
            }
            else if (token is JArray array)
                foreach (var child in array) MapBindings(child, map);
        }

        private string MapDocumentKey(string key, Func<string, string> map)
        {
            var separator = key.IndexOf('|');
            if (separator <= 0) throw new InvalidOperationException("生成记录引用缺少文档配置身份：" + key);
            return map(key.Substring(0, separator)).ToUpperInvariant() + key.Substring(separator);
        }

        private void MapPath(JObject obj, string name, Func<string, string> map)
        {
            var value = obj?.Value<string>(name);
            if (!string.IsNullOrWhiteSpace(value)) obj[name] = map(value);
        }

        private void MapKeys(JObject obj, Func<string, string> map)
        {
            if (obj == null) return;
            var entries = obj.Properties().Select(p => new { Name = map(p.Name), Value = p.Value }).ToArray();
            if (entries.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
                throw new InvalidOperationException("生成记录的相对引用重复，不能唯一绑定文件。");
            // 快照可能包含大量实体几何；重命名索引时移动原节点，避免复制整棵几何树。
            foreach (var property in obj.Properties()) property.Value = JValue.CreateNull();
            obj.RemoveAll();
            foreach (var entry in entries) obj.Add(entry.Name, entry.Value);
        }

        private void MapUpstream(JObject data, string field, string oldAssembly, string newAssembly)
        {
            var text = data.Value<string>(field);
            if (text == null) return;
            var separator = text.IndexOf('\n');
            var recordedAssembly = separator < 0 ? text : text.Substring(0, separator);
            if (!string.Equals(recordedAssembly, oldAssembly, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("生成记录的上游总装身份不匹配：" + field);
            data[field] = newAssembly + (separator < 0 ? string.Empty : text.Substring(separator));
        }

        private JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }
    }
}
