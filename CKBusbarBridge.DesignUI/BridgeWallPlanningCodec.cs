using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CKBusbarBridgePreview;
using GeometRi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeWallPlanningCodec
    {
        internal sealed class WallPlanningData
        {
            public int LengthRuleVersion { get; set; }
            public double? RequestedSpecialSpan { get; set; }
            public int AbsorbedOrdinaryCount { get; set; }
            public string InputFingerprint { get; set; }
            public int PhaseSwitchRuleVersion { get; set; }
            public bool IsPhaseSwitched { get; set; }
            public int WallEndRuleVersion { get; set; }
            public double WallEndClearanceMm { get; set; }
            public double? SpecialStartX { get; set; }
            public double? MinimumSpecialSpan { get; set; }
            public string MinimumSpanScope { get; set; }
            public LinkageRouteWorkerRequestDto Request { get; set; }
            public LinkageRouteWorkerSolutionDto Solution { get; set; }
            public AlgorithmBusbarGenerationGroup[] Groups { get; set; }
            public string[] JointIds { get; set; }
            [JsonIgnore] internal IReadOnlyList<LinkageRouteWorkerSolutionDto> RuntimeCandidates { get; set; }
            [JsonIgnore] internal int RuntimeSelectedIndex { get; set; }
        }
        internal string EncodeWallPlanning(LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, double? specialStartX = null, string[] jointIds = null,
            bool isPhaseSwitched = false, double? minimumSpecialSpan = null) => EncodeWallPlanning(
                new WallPlanningData { PhaseSwitchRuleVersion = 1, IsPhaseSwitched = isPhaseSwitched,
                    LengthRuleVersion = 1,
                    WallEndRuleVersion = 1, WallEndClearanceMm = FaceToWallWallEndRule.ClearanceMm,
                    Request = request, Solution = solution, Groups = groups.ToArray(), SpecialStartX = specialStartX, JointIds = jointIds,
                    MinimumSpecialSpan = minimumSpecialSpan, MinimumSpanScope = minimumSpecialSpan.HasValue ? FaceToWallMinimumSpan.Scope : null });
        internal string EncodeWallPlanning(WallPlanningData data) => JsonConvert.SerializeObject(data, Settings());
        internal WallPlanningData DecodeWallPlanning(string json)
        {
            var data = JsonConvert.DeserializeObject<WallPlanningData>(json, Settings());
            if (data?.Request == null || data.Solution == null || data.Groups == null || data.Groups.Length == 0)
                throw new InvalidOperationException("生成记录缺少面对墙原生方案及编辑拓扑。");
            if (data.LengthRuleVersion < 0 || data.LengthRuleVersion > 1 || data.AbsorbedOrdinaryCount < 0 ||
                data.RequestedSpecialSpan.HasValue && (double.IsNaN(data.RequestedSpecialSpan.Value) ||
                double.IsInfinity(data.RequestedSpecialSpan.Value) || data.RequestedSpecialSpan.Value <= 0))
                throw new InvalidOperationException("生成记录的特殊段长度规则或目标无效，请重新规划铜排。");
            if (data.MinimumSpecialSpan.HasValue && (double.IsNaN(data.MinimumSpecialSpan.Value) ||
                double.IsInfinity(data.MinimumSpecialSpan.Value) || data.MinimumSpecialSpan.Value <= 0))
                throw new InvalidOperationException("生成记录的特殊段最小长度无效，请重新规划铜排。");
            if (data.PhaseSwitchRuleVersion != 0 && data.PhaseSwitchRuleVersion != 1)
                throw new InvalidOperationException("生成记录的面对墙换相规则不支持，请重新规划铜排。");
            if (data.WallEndRuleVersion != 0 && (data.WallEndRuleVersion != 1 ||
                double.IsNaN(data.WallEndClearanceMm) || double.IsInfinity(data.WallEndClearanceMm) ||
                Math.Abs(data.WallEndClearanceMm - FaceToWallWallEndRule.ClearanceMm) > .01d))
                throw new InvalidOperationException("生成记录的墙端预留规则不支持，请重新规划铜排。");
            return data;
        }

        internal string InputFingerprint(FaceToWallUpstreamGeometry input)
        {
            var json = JsonConvert.SerializeObject(new { input.RootCount, input.Clearance, input.OverlapLength,
                Origin = input.Coordinates.Origin, Along = input.Coordinates.Along, Across = input.Coordinates.Across, Up = input.Coordinates.Up,
                input.IsPhaseSwitched, input.BaseInstallationSeamX, input.AppliedWallVerticalClearance,
                input.TargetWallEnd, input.TargetWallWidth, input.TargetWallHeight, input.TargetWallCenterAcross,
                input.TargetWallCenterHeight, input.Cabinet.BaseWidth, input.Cabinet.OpeningHeight,
                Opening = Point(input.Cabinet.Opening), ConnectionHeight = input.Cabinet.CalculateConnectionHeight(),
                Corners = input.WallOpeningCorners.Select(Point).OrderBy(p => p[0]).ThenBy(p => p[1]).ThenBy(p => p[2]).ToArray(),
                Cabinet = input.Cabinet.Extensions.Select(b => b.Phase + ":" + Setting(b.Shape)).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                Wall = input.WallConductors.Select(b => Setting(b.Shape)).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                Connections = input.Cabinet.OrdinaryConnections.Select(c => new { Center = Point(c.Center),
                    SolidCenter = Point(c.SolidCenter), Normal = new[] { c.Normal.X, c.Normal.Y, c.Normal.Z }, c.Radius })
                    .OrderBy(c => c.Center[0]).ThenBy(c => c.Center[1]).ThenBy(c => c.Center[2]).ToArray() });
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(json)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private double[] Point(Point3d point) => new[] { point.X, point.Y, point.Z };
        private string Setting(PlanarBendBusbarSetting bar) => JsonConvert.SerializeObject(new { bar.IsCenter, bar.IsMilledBar, bar.IsVerticalBend,
            bar.BusBarWidth, bar.BusBarThickness, bar.StartLapWidth, bar.EndLapWidth, bar.BarBendingRadius, bar.BendAllowance,
            Points = bar.PointList.Select(Point).ToArray(), Width = new[] { bar.WidthVector.X, bar.WidthVector.Y, bar.WidthVector.Z },
            Normal = new[] { bar.StartBusbarVector.X, bar.StartBusbarVector.Y, bar.StartBusbarVector.Z } });
        private JsonSerializerSettings Settings() => CreateSettings();
        internal static JsonSerializerSettings CreateSettings() => new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture, ObjectCreationHandling = ObjectCreationHandling.Replace,
            TypeNameHandling = TypeNameHandling.None, ContractResolver = new StoredDataResolver(),
            Converters = { new GeometryConverter() }
        };

        private sealed class GeometryConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(Point3d) || type == typeof(Vector3d);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var point = value as Point3d;
                var vector = value as Vector3d;
                serializer.Serialize(writer, point != null ? new[] { point.X, point.Y, point.Z } : new[] { vector.X, vector.Y, vector.Z });
            }
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var xyz = JArray.Load(reader).ToObject<double[]>();
                if (xyz.Length != 3 || xyz.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    throw new JsonSerializationException("生成记录包含无效几何坐标。");
                return type == typeof(Point3d) ? (object)new Point3d(xyz[0], xyz[1], xyz[2]) : new Vector3d(xyz[0], xyz[1], xyz[2]);
            }
        }
        private sealed class StoredDataResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
                => base.CreateProperties(type, serialization).Where(p => p.Writable).ToList();
        }
    }
}
