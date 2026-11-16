using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CKBusbarBridge.Prototype
{
    internal static class BridgeWallConductorSnapshotReader
    {
        // Reuse the original I-bar recognizer with frozen planes and straight edges. Component/Body/Face COM references stay null.
        internal static JArray Read(JObject input)
        {
            var result = new JArray();
            var settings = BridgeWallPlanningCodec.CreateSettings(); settings.ContractResolver = new DefaultContractResolver();
            var serializer = JsonSerializer.Create(settings);
            foreach (var component in input["components"])
            {
                var name = (string)component["name"];
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("套管几何缺少原组件身份。");
                var model = new ComponentInto { Name = name };
                model.BodyIntos = component["bodies"].Select(body => new BodyInto { ComponentInto = model,
                    FaceIntos = body["faces"].Select(face => new FaceInto { ComponentInto = model, FaceName = (string)face["identity"],
                        IsPlane = true, FacePlane = new Plane3d(Point(face["point"]), Vector(face["normal"])),
                        EdgeIntos = face["edges"].Select(edge => new EdgeInto { CurveType = SwCurveType.Line,
                            Segment = new Segment3d(Point(edge[0]), Point(edge[1])) }).ToList() }).ToList() }).ToList();
                var bars = WallBushingBusbarReader.AnalyzeSnapshot(model, BusbarBridgeAxisSetting.CreateDefault());
                if (bars.Count == 0) throw new InvalidOperationException("穿墙套管没有识别出原I排接口：" + name);
                for (var index = 0; index < bars.Count; index++)
                {
                    var bar = bars[index];
                    result.Add(new JObject { ["id"] = name + ":" + index, ["phase"] = bar.Phase.ToString(),
                        ["shape"] = JObject.FromObject(bar.CenterlineSetting, serializer),
                        ["terminalStart"] = Values(bar.TerminalSegment.P1), ["terminalEnd"] = Values(bar.TerminalSegment.P2) });
                }
            }
            return result;
        }
        private static Point3d Point(JToken value) => new Point3d(SinglePrecision.Number(value[0]), SinglePrecision.Number(value[1]), SinglePrecision.Number(value[2]));
        private static Vector3d Vector(JToken value) => new Vector3d(SinglePrecision.Number(value[0]), SinglePrecision.Number(value[1]), SinglePrecision.Number(value[2]));
        private static JArray Values(Point3d point) => new JArray((float)point.X, (float)point.Y, (float)point.Z);
    }
}
