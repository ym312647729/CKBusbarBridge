using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal sealed class BridgeDimensionEdge
    {
        internal IReadOnlyList<PreviewPoint3> Points { get; }
        internal PreviewPoint3 Start => Points[0];
        internal PreviewPoint3 End => Points[Points.Count - 1];
        internal BridgeDimensionEdge(PreviewPoint3 start, PreviewPoint3 end) : this(new[] { start, end }) { }
        internal BridgeDimensionEdge(IEnumerable<PreviewPoint3> points) { Points = Array.AsReadOnly(points.ToArray()); }
    }

    internal sealed class BridgePreviewDimension
    {
        internal string ObjectId { get; }
        internal DesignField Field { get; }
        internal string Label { get; }
        internal float Value { get; }
        internal long Revision { get; }
        internal string ParameterPath { get; }
        internal bool IsReadOnly { get; }
        internal bool IsCabinetSpacing => ParameterPath == "General.CabinetSpacing" || ParameterPath == "General.CabinetToWallDistance";
        internal bool IsCabinetOffset => ParameterPath == "General.CabinetEccentricity" || ParameterPath == "General.WallCabinetEccentricity";
        internal IReadOnlyList<BridgeDimensionEdge> Edges { get; }
        internal bool MatchesField(BridgeDesignFieldVM field) => field != null && ParameterPath == null &&
            Field == field.Field && string.Equals(ObjectId, field.ObjectId, StringComparison.Ordinal);
        internal string Text => Value.ToString("0.#", CultureInfo.InvariantCulture);
        internal static string DimensionName(string label) => label.Replace(" (mm)", "").Replace("（mm）", "")
            .Replace("，mm）", "）").Replace(" (°)", "").Replace("（°）", "");
        internal BridgePreviewDimension(BridgeDesignFieldVM field, float value, long revision, IEnumerable<BridgeDimensionEdge> edges)
        { ObjectId = field.ObjectId; Field = field.Field; Label = DimensionName(field.Label); Value = value; Revision = revision; Edges = edges.ToArray(); IsReadOnly = field.IsReadOnly; }
        internal BridgePreviewDimension(string objectId, string parameterPath, string label, float value, long revision, bool editable, IEnumerable<BridgeDimensionEdge> edges)
        { ObjectId = objectId; ParameterPath = parameterPath; Label = DimensionName(label); Value = value; Revision = revision; IsReadOnly = !editable; Edges = edges.ToArray(); }
    }

    // Cabinet and base dimensions are shown in full while designing cabinets. While the bridge
    // editor is attached they appear only for the selected cabinet/base. Cabinet references match
    // their exact mesh identity; base supports can also match their parent through the id chain.
    internal static class BridgeCabinetDimensionVisibility
    {
        internal static IEnumerable<BridgePreviewDimension> Visible(IEnumerable<BridgePreviewDimension> dimensions, bool bridgeEditing, IEnumerable<string> selectedIds)
        {
            if (!bridgeEditing) return dimensions;
            var selected = selectedIds.Where(id => !string.IsNullOrEmpty(id)).ToArray();
            return selected.Length == 0 ? Enumerable.Empty<BridgePreviewDimension>()
                : dimensions.Where(dimension => selected.Any(id => SameIdentityChain(id, dimension.ObjectId)));
        }
        internal static bool SameIdentityChain(string selectedId, string ownerId)
            => !string.IsNullOrEmpty(ownerId) && (string.Equals(selectedId, ownerId, StringComparison.Ordinal) ||
                selectedId.StartsWith(ownerId + ":", StringComparison.Ordinal) || ownerId.StartsWith(selectedId + ":", StringComparison.Ordinal));
    }

    internal sealed class BridgeDimensionEditRequest
    {
        internal BridgePreviewDimension Dimension { get; }
        internal int ScreenX { get; }
        internal int ScreenY { get; }
        internal BridgeDimensionEditRequest(BridgePreviewDimension dimension, int screenX, int screenY)
        { Dimension = dimension; ScreenX = screenX; ScreenY = screenY; }
    }

    /// <summary>仅使用原驱动尺寸、模板坐标和原编辑基准，不从网格包围盒推断标注。</summary>
    internal sealed class BridgePreviewDimensionFactory
    {
        private readonly BridgeDesignSample _sample;
        private readonly IReadOnlyList<BridgeDesignObject> _objects;
        private readonly BusbarBridgeDraftEditor _editor;
        private readonly BridgeUpstreamSnapshot _upstream;
        private readonly BusbarBridgeDraftPreviewSceneFactory _geometry = new BusbarBridgeDraftPreviewSceneFactory();
        private readonly BridgeCoordinateFrame _coordinates;
        internal BridgePreviewDimensionFactory(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            BusbarBridgeDraftEditor editor, BridgeUpstreamSnapshot upstream = null)
        {
            _sample = sample; _objects = objects; _editor = editor; _upstream = upstream;
            _coordinates = sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value);
        }

        internal static bool IsDimension(DesignField field)
        {
            switch (field)
            {
                case DesignField.Length: case DesignField.Width: case DesignField.Height:
                case DesignField.InsulatorPosition: case DesignField.InsulatorDistanceFromPrevious:
                case DesignField.CopperSegmentLength: case DesignField.JointDistanceFromPrevious:
                case DesignField.BaseJointEdgeDistance: case DesignField.WallEntryDistance:
                case DesignField.WallVerticalClearance: case DesignField.WallHorizontalClearance: return true;
                default: return false;
            }
        }

        internal BridgePreviewDimension Create(BridgeDesignFieldVM field)
        {
            if (!IsDimension(field.Field)) return null;
            if (!float.TryParse(field.CommittedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("可编辑尺寸缺少有效值：" + field.ObjectId + "/" + field.Label);
            var node = _editor.Find(field.ObjectId);
            List<BridgeDimensionEdge> edges;
            if (field.Field == DesignField.CopperSegmentLength)
            {
                var section = _editor.FindCopperSection(field.ObjectId);
                edges = CopperEdges(section, _editor.Find(section.StartPointId), _editor.Find(section.EndPointId), value, true);
            }
            else if (field.Field == DesignField.JointDistanceFromPrevious)
            {
                var section = _editor.CopperSections.First(item => item.EndPointId == node.Id);
                edges = CopperEdges(section, _editor.Find(section.StartPointId), node, value, false);
            }
            else if (field.Field == DesignField.BaseJointEdgeDistance)
            {
                var end = _geometry.WorldPosition(node, _objects); var start = (double[])end.Clone();
                start[0] = _editor.PreviewBaseBoundaryX(node.Id) ?? throw new InvalidOperationException("基座边基准缺失。");
                edges = new List<BridgeDimensionEdge> { Edge(start, end) };
            }
            else if (field.Field == DesignField.InsulatorPosition || field.Field == DesignField.InsulatorDistanceFromPrevious)
                edges = InsulatorEdges(node, field.Field);
            else if (field.Field == DesignField.Length || field.Field == DesignField.Width || field.Field == DesignField.Height)
                edges = FrameEdges(node, field.Field);
            else edges = WallEdges(node, field.Field);
            return new BridgePreviewDimension(field, value, _editor.Revision, edges);
        }

        private List<BridgeDimensionEdge> FrameEdges(BridgeDesignObject frame, DesignField field)
        {
            var result = new List<BridgeDimensionEdge>();
            var sizes = new[] { frame.DriveValue(DesignField.Length), frame.DriveValue(DesignField.Width), frame.DriveValue(DesignField.Height) };
            int axis = field == DesignField.Length ? 0 : field == DesignField.Width ? 1 : 2;
            foreach (double side in new[] { -1d, 1d }) foreach (double face in new[] { -1d, 1d })
            {
                var start = new double[3]; start[axis] = -sizes[axis] / 2;
                start[(axis + 1) % 3] = side * sizes[(axis + 1) % 3] / 2;
                start[(axis + 2) % 3] = face * sizes[(axis + 2) % 3] / 2;
                var end = (double[])start.Clone(); end[axis] = sizes[axis] / 2;
                result.Add(Edge(FramePoint(frame, start), FramePoint(frame, end)));
            }
            return result;
        }

        private List<BridgeDimensionEdge> InsulatorEdges(BridgeDesignObject node, DesignField field)
        {
            var frame = _editor.Find(node.ParentId) ?? throw new InvalidOperationException("绝缘子缺少所属基架。");
            if (field == DesignField.InsulatorDistanceFromPrevious) return InsulatorStationEdges(node);
            var result = new List<BridgeDimensionEdge>();
            foreach (double side in new[] { -1d, 1d }) foreach (double face in new[] { -1d, 1d })
            {
                var start = FramePoint(frame, new[] { -frame.Length / 2, side * frame.Width / 2, face * frame.Height / 2 });
                var end = FramePoint(frame, new[] { node.X, side * frame.Width / 2, face * frame.Height / 2 });
                result.Add(Edge(start, end));
            }
            return result;
        }

        private List<BridgeDimensionEdge> InsulatorStationEdges(BridgeDesignObject node)
        {
            var item = _editor.InsulatorObjects.First(part => part.MemberIds.Contains(node.Id));
            var previous = _editor.InsulatorObjects.First(part => part.Id == item.PreviousId);
            if (!previous.Station.HasValue || !item.Station.HasValue || item.Error != null)
                throw new InvalidOperationException("前绝缘子安装里程缺失。");
            // 与 BridgeInsulatorObjects 相同的入口—中心—出口里程；转角处不能用两端直线距离代替。
            var route = new List<double[]>(); var station = 0d;
            var points = new List<double[]>();
            foreach (var owner in _objects.Where(part => part.Kind == DesignObjectKind.Frame).OrderBy(part => part.FramePositionIndex))
            {
                var ports = _geometry.FrameInterfacePoints(owner);
                points.Add(ports[0]); points.Add(new[] { owner.X, owner.Y, owner.Z }); points.Add(ports[1]);
            }
            for (int index = 1; index < points.Count; index++)
            {
                var start = points[index - 1]; var end = points[index];
                double length = Math.Sqrt(Enumerable.Range(0, 3).Sum(axis => Math.Pow(end[axis] - start[axis], 2)));
                if (length < 1.0f) continue;
                double from = Math.Max(0, previous.Station.Value - station);
                double to = Math.Min(length, item.Station.Value - station);
                if (to > from + 1.0f)
                {
                    Func<double, double[]> along = distance => Enumerable.Range(0, 3)
                        .Select(axis => start[axis] + (end[axis] - start[axis]) * distance / length).ToArray();
                    if (route.Count == 0) route.Add(along(from));
                    route.Add(along(to));
                }
                station += length;
                if (station >= item.Station.Value - 1.0f) break;
            }
            if (route.Count < 2) throw new InvalidOperationException("绝缘子安装路径无有效标注段。");
            return new List<BridgeDimensionEdge> { new BridgeDimensionEdge(route.Select(ToPoint)) };
        }

        private List<BridgeDimensionEdge> CopperEdges(BridgeCopperSection section, BridgeDesignObject startNode,
            BridgeDesignObject endNode, double length, bool includeLap)
        {
            var start = _geometry.WorldPosition(startNode, _objects);
            double sign = endNode.X >= startNode.X ? 1 : -1;
            double offset = includeLap ? _sample.Parameters.OverlapLengthMm / 2 : 0;
            start[0] -= sign * offset;
            var result = new List<BridgeDimensionEdge>();
            // 按原分段身份取实际模板棱边。横截面方向随安装变化，不能固定成全局Y宽/Z厚，也不读取Box。
            var edgeIndices = new[] { new[] {0,1}, new[] {1,2}, new[] {2,3}, new[] {3,0},
                new[] {4,5}, new[] {5,6}, new[] {6,7}, new[] {7,4},
                new[] {0,4}, new[] {1,5}, new[] {2,6}, new[] {3,7} };
            var routes = _sample.Routes.Where(route => route.CopperSectionId == section.Id).ToArray();
            foreach (var route in routes)
            {
                var candidates = route.PhysicalSolids.Where(solid => solid.Length == 8)
                    .SelectMany(solid => edgeIndices.Select(indices => new[] { solid[indices[0]], solid[indices[1]] }))
                    .Where(pair => Math.Abs(pair[1][0] - pair[0][0]) > 1.0f &&
                        Math.Abs(pair[1][1] - pair[0][1]) < 1.0f && Math.Abs(pair[1][2] - pair[0][2]) < 1.0f)
                    .ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException("铜排原始桥向棱边缺失：" + route.Id);
                double longest = candidates.Max(pair => Math.Abs(pair[1][0] - pair[0][0]));
                foreach (var pair in candidates.Where(pair => Math.Abs(pair[1][0] - pair[0][0]) >= longest - 1.0f))
                    result.Add(Edge(new[] { start[0], pair[0][1], pair[0][2] },
                        new[] { start[0] + sign * length, pair[0][1], pair[0][2] }));
            }
            if (result.Count == 0) throw new InvalidOperationException("铜排分段缺少原始预览身份：" + section.Id);
            return result;
        }

        private List<BridgeDimensionEdge> WallEdges(BridgeDesignObject frame, DesignField field)
        {
            var corners = _upstream?.FaceToWall?.WallOpeningCorners ?? throw new InvalidOperationException("墙孔原始边界缺失，无法标注。");
            var result = new List<BridgeDimensionEdge>();
            foreach (double sign in new[] { -1d, 1d })
            {
                var end = FramePoint(frame, new[] { frame.Length / 2,
                    field == DesignField.WallHorizontalClearance ? sign * frame.Width / 2 : 0,
                    field == DesignField.WallVerticalClearance ? sign * frame.Height / 2 : 0 });
                var start = (double[])end.Clone();
                if (field == DesignField.WallEntryDistance) start[0] = corners.Average(point => point.X);
                if (field == DesignField.WallHorizontalClearance) start[1] = sign < 0 ? corners.Min(point => point.Z) : corners.Max(point => point.Z);
                if (field == DesignField.WallVerticalClearance) start[2] = sign < 0 ? corners.Min(point => point.Y) : corners.Max(point => point.Y);
                result.Add(Edge(start, end));
            }
            return result;
        }

        private double[] FramePoint(BridgeDesignObject frame, double[] local)
        {
            var origin = _geometry.WorldPosition(frame, _objects);
            var parent = _editor.Find(frame.ParentId);
            double angle = (frame.ModelDirection + (parent?.ModelDirection ?? 0)) * Math.PI / 180;
            return new[] { origin[0] + local[0] * Math.Cos(angle) - local[1] * Math.Sin(angle),
                origin[1] + local[0] * Math.Sin(angle) + local[1] * Math.Cos(angle), origin[2] + local[2] };
        }
        private PreviewPoint3 ToPoint(double[] draft)
        {
            var assembly = _coordinates.ToAssembly(draft);
            return new PreviewPoint3(assembly[0] / 1000, assembly[1] / 1000, assembly[2] / 1000);
        }
        private BridgeDimensionEdge Edge(double[] start, double[] end)
        {
            return new BridgeDimensionEdge(ToPoint(start), ToPoint(end));
        }
    }
}
