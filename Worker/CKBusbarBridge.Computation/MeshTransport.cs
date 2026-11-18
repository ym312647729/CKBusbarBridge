using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Computation
{
    internal static class MeshTransport
    {
        internal static void Expand(JObject response)
        {
            foreach (var collection in new[] { "instances", "interactionInstances" })
                foreach (var entry in response[collection] as JArray ?? new JArray())
                {
                    if (!(entry["packedGeometry"] is JObject packed)) continue;
                    if ((string)packed["encoding"] != "mesh32-le/1" || !(packed["fields"] is JObject fields))
                        throw new InvalidOperationException("预览网格传输格式不兼容。");
                    foreach (var field in fields.Properties())
                    {
                        var coordinates = field.Name == "positions" || field.Name == "edgePositions";
                        if (!coordinates && field.Name != "quads" && field.Name != "triangles" && field.Name != "edges")
                            throw new InvalidOperationException("预览网格包含未知字段：" + field.Name);
                        var bytes = Convert.FromBase64String((string)field.Value);
                        if (bytes.Length % 4 != 0) throw new InvalidOperationException("预览网格传输长度无效。");
                        var values = new List<JToken>(bytes.Length / 4);
                        for (var offset = 0; offset < bytes.Length; offset += 4)
                        {
                            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes, offset, 4);
                            if (coordinates)
                            {
                                var value = (double)BitConverter.ToSingle(bytes, offset);
                                if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("预览网格坐标无效。");
                                // 与原 JSON 的数字类型一致，整值坐标仍作为整数返回。
                                values.Add(value >= long.MinValue && value < long.MaxValue && value == Math.Truncate(value)
                                    ? new JValue((long)value) : new JValue(value));
                            }
                            else values.Add(new JValue((long)BitConverter.ToUInt32(bytes, offset)));
                        }
                        entry[field.Name] = new JArray(values);
                    }
                    ((JObject)entry).Remove("packedGeometry");
                }
        }
    }
}
