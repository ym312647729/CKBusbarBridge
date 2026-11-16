using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeInsulatorObject
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string FrameId { get; internal set; }
        public string FrameName { get; internal set; }
        public int FramePositionIndex { get; internal set; }
        public string FrameLabel => FramePositionIndex > 0 ? FrameName + "（位置" + FramePositionIndex + "）" : "归属未核验";
        public int Count { get; internal set; }
        public double? Position { get; internal set; }
        public string PositionText => Position?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        public string Error { get; internal set; }
        internal string[] MemberIds { get; set; }
        internal string PreviousId { get; set; }
        internal double? CenterStation { get; set; }
        internal double? Station { get; set; }
        internal double? DistanceFromPrevious { get; set; }
    }

    // 一次草稿快照只计算一次；列表绑定和选择查询不重新遍历模板几何。
    internal sealed class BridgeInsulatorObjects
    {
        private IReadOnlyList<BridgeDesignObject> _source;
        private IReadOnlyList<BridgeInsulatorObject> _items = Array.Empty<BridgeInsulatorObject>();
        private readonly BusbarBridgeDraftPreviewSceneFactory _geometry = new BusbarBridgeDraftPreviewSceneFactory();

        internal static string GroupProblem(IReadOnlyList<BridgeDesignObject> members)
        {
            if (members.Count == 0) return "绝缘子安装组为空。";
            var first = members[0];
            if (string.IsNullOrEmpty(first.InsulatorGroupId) || members.Any(o => o.Kind != DesignObjectKind.Insulator ||
                o.ParentId != first.ParentId || o.InsulatorGroupId != first.InsulatorGroupId))
                return "绝缘子安装标识或所属基架无法唯一核验：" + first.Id;
            if (members.Any(BridgeSmallOffsetSupportPlanner.IsSupport))
            {
                if (members.Count != 3 || !members.All(BridgeSmallOffsetSupportPlanner.IsSupport) ||
                    members.Select(o => o.MountGroupIndex).Distinct().Count() != 3)
                    return "落地支撑安装组缺少完整的三个模板位置：" + first.InsulatorGroupId;
                if (members.Count(o => !o.IsSuppressedInsulator) != 1)
                    return "落地支撑安装组必须且只能启用一个绝缘子：" + first.InsulatorGroupId;
            }
            // 吊装组在当前站位可能尚无可用支承面，仍须允许用户选中并移动到合法站位。
            return null;
        }

        internal IReadOnlyList<BridgeInsulatorObject> Read(IReadOnlyList<BridgeDesignObject> source)
        {
            if (ReferenceEquals(_source, source)) return _items;
            var frames = source.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            var error = ReadStations(frames.Values.ToArray(), out var stations);
            var items = new List<BridgeInsulatorObject>();
            foreach (var group in source.Where(o => o.Kind == DesignObjectKind.Insulator)
                .GroupBy(o => o.InsulatorGroupId ?? o.Id, StringComparer.Ordinal))
            {
                var members = group.ToArray();
                var parent = members[0].ParentId;
                var frame = parent != null && frames.TryGetValue(parent, out var owner) ? owner : null;
                var ordered = members.OrderBy(o => frame?.FrameTemplateKey == "SideToSidePhaseChange029" ? o.Z : o.Y)
                    .ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
                var representative = ordered.Any(o => o.MountPlan != null) ? ordered.FirstOrDefault(o => !o.IsSuppressedInsulator) ?? ordered[0] :
                    ordered.FirstOrDefault(o => o.Phase == "B") ?? ordered[ordered.Length / 2];
                var item = new BridgeInsulatorObject { Id = representative.Id, MemberIds = members.Select(o => o.Id).ToArray(),
                    FrameId = parent, FrameName = frame?.Name, FramePositionIndex = frame?.FramePositionIndex ?? 0,
                    Count = members.Count(o => !o.IsSuppressedInsulator), Error = GroupProblem(members) };
                if (item.Error == null && frame == null)
                    item.Error = "绝缘子安装标识或所属基架无法唯一核验：" + representative.Id;
                else if (item.Error == null && members.Any(o => !Finite(o.X) || Math.Abs(o.X - representative.X) > 1.0))
                    item.Error = "绝缘子内部定位不一致：" + representative.Id;
                else if (item.Error == null)
                {
                    item.Position = representative.X + frame.Length / 2;
                    if (stations.TryGetValue(frame.Id, out var station))
                    {
                        item.CenterStation = station;
                        item.Station = station + (frame.IsReversed ? -1 : 1) * representative.X;
                    }
                }
                items.Add(item);
            }
            error = error ?? items.Select(o => o.Error).FirstOrDefault(e => e != null);
            var result = items.OrderBy(o => o.Station ?? double.MaxValue).ThenBy(o => o.FramePositionIndex)
                .ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            for (var i = 1; i < result.Length; i++)
                if (result[i].Station.HasValue && result[i - 1].Station.HasValue &&
                    result[i].Station.Value - result[i - 1].Station.Value <= .01)
                    error = "绝缘子安装位置重合，无法唯一确定桥向前后关系。";
            for (var i = 0; i < result.Length; i++)
            {
                var item = result[i];
                item.Name = "绝缘子" + (i + 1).ToString(CultureInfo.InvariantCulture);
                item.Error = item.Error ?? error;
                if (i == 0 || error != null) continue;
                item.PreviousId = result[i - 1].Id;
                item.DistanceFromPrevious = item.Station - result[i - 1].Station;
            }
            _source = source;
            return _items = Array.AsReadOnly(result);
        }

        private string ReadStations(BridgeDesignObject[] frames, out Dictionary<string, double> centers)
        {
            centers = new Dictionary<string, double>(StringComparer.Ordinal);
            if (frames.Any(o => o.FramePositionIndex <= 0) || frames.Select(o => o.FramePositionIndex).Distinct().Count() != frames.Length)
                return "基架位置号缺失或重复，不能计算绝缘子桥向定位。";
            var station = 0d;
            double[] previousOutlet = null;
            foreach (var frame in frames.OrderBy(o => o.FramePositionIndex))
            {
                if (!new[] { "Regular", "SmallOffset", "LargeOffsetCorner", "FaceToWallLarge1", "FaceToWallLarge2", "SideToSidePhaseChange029" }.Contains(frame.FrameTemplateKey) ||
                    frame.FrameTemplateKey == "SmallOffset" && frame.SmallOffsetPreview == null ||
                    frame.FrameTemplateKey == "LargeOffsetCorner" && frame.CornerPreview == null ||
                    (frame.FrameTemplateKey == "FaceToWallLarge1" || frame.FrameTemplateKey == "FaceToWallLarge2") && frame.WallFramePreview == null)
                    return "基架缺少已核验的模板接口参数：" + frame.DisplayName;
                if (frame.Length <= 0 || !new[] { frame.X, frame.Y, frame.Z, frame.Length, frame.Direction }.All(Finite))
                    return "基架安装参数无效：" + frame.DisplayName;
                var ports = _geometry.FrameInterfacePoints(frame);
                if (ports.Length != 2 || ports.Any(p => p.Length != 3 || !p.All(Finite)))
                    return "基架安装接口无效：" + frame.DisplayName;
                var center = new[] { frame.X, frame.Y, frame.Z };
                // 与现有绝缘子规划使用同一模板接口里程，不能用全局X或Box替代。
                if (previousOutlet != null) station += Distance(previousOutlet, ports[0]);
                var centerStation = station + Distance(ports[0], center);
                centers[frame.Id] = centerStation;
                station = centerStation + Distance(center, ports[1]);
                previousOutlet = ports[1];
                if (!Finite(station)) return "基架累计安装里程无效：" + frame.DisplayName;
            }
            return null;
        }

        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private double Distance(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));
    }
}
