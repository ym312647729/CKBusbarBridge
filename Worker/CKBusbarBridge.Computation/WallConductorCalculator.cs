using System;
using System.Linq;
using GeometRi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CKBusbarBridge.Computation.WallGeometry;

namespace CKBusbarBridge.Computation
{
    internal static class WallConductorCalculator
    {
        internal static JToken Read(JObject input)
        {
            var result = new JArray();
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new GeometryConverter() } });
            foreach (var component in input["components"])
            {
                var name = (string)component["name"];
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("套管几何缺少原组件身份。");
                var model = new ComponentInto { Name = name,
                    BodyIntos = component["bodies"].Select(body => new BodyInto {
                        FaceIntos = body["faces"].Select(face => new FaceInto {
                            IsPlane = true, FacePlane = new Plane3d(Point(face["point"]), Vector(face["normal"])),
                            EdgeIntos = face["edges"].Select(edge => new EdgeInto {
                                CurveType = SwCurveType.Line, Segment = new Segment3d(Point(edge[0]), Point(edge[1])) }).ToList()
                        }).ToList()
                    }).ToList() };
                var bars = WallBushingBusbarReader.AnalyzeVirtualComponent(model, BusbarBridgeAxisSetting.CreateDefault());
                if (bars.Count == 0) throw new InvalidOperationException("穿墙套管没有识别出原 I 排接口：" + name);
                for (var index = 0; index < bars.Count; index++)
                    result.Add(new JObject { ["id"] = name + ":" + index, ["phase"] = "Unknow",
                        ["shape"] = JObject.FromObject(bars[index].CenterlineSetting, serializer),
                        ["terminalStart"] = Values(bars[index].TerminalSegment.P1), ["terminalEnd"] = Values(bars[index].TerminalSegment.P2) });
            }
            return result;
        }
        private static Point3d Point(JToken value) => new Point3d(value[0].Value<float>(), value[1].Value<float>(), value[2].Value<float>());
        private static Vector3d Vector(JToken value) => new Vector3d(value[0].Value<float>(), value[1].Value<float>(), value[2].Value<float>());
        private static JArray Values(Point3d point) => new JArray((float)point.X, (float)point.Y, (float)point.Z);
        private sealed class GeometryConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(Point3d) || type == typeof(Vector3d);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is Point3d point) serializer.Serialize(writer, new[] { point.X, point.Y, point.Z });
                else { var vector = (Vector3d)value; serializer.Serialize(writer, new[] { vector.X, vector.Y, vector.Z }); }
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
                => throw new NotSupportedException();
        }
    }
}
