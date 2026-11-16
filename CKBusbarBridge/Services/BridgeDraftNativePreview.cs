using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeDraftPreviewSnapshot
    {
        public long Revision { get; }
        private readonly PlanarBendBusbarSetting[] _copper;
        public IReadOnlyList<DraftSupportSolid> Frames { get; }
        public IReadOnlyList<DraftSupportSolid> Insulators { get; }
        public IReadOnlyList<DraftSupportSolid> Hangers { get; }
        public PlanarBendBusbarSetting[] CopyCopper() => _copper.Select(p => BridgeNativePreviewCopy.Copy(p)).ToArray();

        public BridgeDraftPreviewSnapshot(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision)
            : this(sample, objects, revision, Array.Empty<DraftSupportSolid>()) { }

        public BridgeDraftPreviewSnapshot(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision,
            IEnumerable<DraftSupportSolid> hangers)
        {
            if (sample?.Source.IsModelDerived != true || !sample.PreviewOriginX.HasValue || !sample.PreviewOriginZ.HasValue)
                throw new InvalidOperationException("3D预览需要从当前总装读取并规划的模型草稿。");
            Revision = revision;
            Hangers = Array.AsReadOnly(hangers.ToArray());
            _copper = sample.Routes.Select(r => r.CopyNativePreview() ??
                throw new InvalidOperationException("铜排缺少原始实体生成参数：" + r.Id)).ToArray();
            if (_copper.Length == 0) throw new InvalidOperationException("当前草稿没有可预览铜排。");
            foreach (var insulator in objects.Where(o => o.Kind == DesignObjectKind.Insulator))
                if (!objects.Any(frame => frame.Id == insulator.ParentId && frame.Kind == DesignObjectKind.Frame))
                    throw new InvalidOperationException("绝缘子缺少所属基架：" + insulator.Id);
            foreach (var group in objects.Where(o => o.Kind == DesignObjectKind.Insulator).GroupBy(o => o.InsulatorGroupId))
                if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != PrototypeBusbarBridgeDesignProvider.InsulatorsPerAssembly)
                    throw new InvalidOperationException("架内绝缘子套不完整：" + group.Key);
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            var coordinates = sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value);
            Func<BridgeDesignObject, bool, IEnumerable<DraftSupportSolid>> convert = (node, bracket) =>
                (bracket ? geometry.InsulatorBracketSolids(node, objects) : geometry.Solids3D(node, objects))
                .Select((solid, index) => solid.Round != null
                    ? new DraftSupportSolid(node.Id + "/round/" + index, solid.Material,
                        solid.Round.Transform(p => coordinates.ToAssembly(p)), node.Id)
                    : new DraftSupportSolid(node.Id + (bracket ? "/bracket/" : "/solid/") + index,
                        solid.Material, solid.Corners.Select(p => coordinates.ToAssembly(p))));
            Frames = Array.AsReadOnly(objects.Where(o => o.Kind == DesignObjectKind.Frame)
                .OrderBy(o => o.Id, StringComparer.Ordinal).SelectMany(o => convert(o, false)).ToArray());
            // 原模型基座内的绝缘子不重复生成；只读取当前有效架内套，不按推荐数量补齐。
            Insulators = Array.AsReadOnly(objects.Where(o => o.Kind == DesignObjectKind.Insulator)
                .OrderBy(o => o.Id, StringComparer.Ordinal).SelectMany(o => convert(o, false).Concat(convert(o, true))).ToArray());
        }

        public static double[] ToAssembly(double[] point, double originX, double originZ)
            => new[] { point[0] + originX, point[2], point[1] + originZ };

        internal static string PreviewReferenceComponentName(BridgeUpstreamSnapshot upstream)
        {
            if (upstream == null) throw new ArgumentNullException(nameof(upstream));
            if (upstream.FaceToWall != null)
            {
                if (string.IsNullOrWhiteSpace(upstream.FaceToWall.PreviewReferenceComponentName))
                    throw new InvalidOperationException("面对墙缺少已反读的伸出排显示组件。");
                return upstream.FaceToWall.PreviewReferenceComponentName;
            }
            if (upstream.SideToSide != null)
            {
                if (string.IsNullOrWhiteSpace(upstream.SideToSide.PreviewReferenceComponentName))
                    throw new InvalidOperationException("侧对侧缺少已反读的柜顶伸出排显示组件，请重读当前总装。");
                return upstream.SideToSide.PreviewReferenceComponentName;
            }
            return upstream.PreviewParts.Where(p => !p.IsInsulator)
                .OrderBy(p => p.Side == "B" ? 0 : 1).ThenBy(p => p.ComponentName, StringComparer.Ordinal)
                .Select(p => p.ComponentName).FirstOrDefault();
        }

        public static string UpstreamFingerprint(BridgeUpstreamSnapshot source)
        {
            Func<double, string> number = v => v.ToString("R", CultureInfo.InvariantCulture);
            return string.Join("\n", new[] { source.ModelPath, source.Configuration, source.Scenario }
                .Concat(source.Fields.OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ThenBy(f => f.Value, StringComparer.Ordinal).ThenBy(f => f.Source, StringComparer.Ordinal)
                    .Select(f => f.Name + "\t" + f.Value + "\t" + f.Source + "\t" + f.IsMissing))
                .Concat(source.Endpoints.OrderBy(e => e.Side).ThenBy(e => e.Phase).ThenBy(e => e.Member)
                    .Select(e => e.Side + e.Phase + e.Member + ":" + string.Join(",", e.Points.SelectMany(p => p).Select(number))))
                .Concat(SideSignature(source.SideToSide)).Concat(WallSignature(source.FaceToWall)));
        }

        // 指纹仍保留原始数值，兼容已有记录，也给失败诊断保留完整精度。
        // 只容许固定端点的 COM 尾差；不能用显示精度舍入来隐藏真实接口移动。
        internal const double UpstreamCoordinateNoiseMm = .000001d;

        internal static bool UpstreamFingerprintsMatch(string expected, string current)
            => UpstreamFingerprintDifferences(expected, current).Count == 0;

        internal static IReadOnlyList<string> UpstreamFingerprintDifferences(string expected, string current)
        {
            if (string.Equals(expected, current, StringComparison.Ordinal)) return Array.Empty<string>();
            if (expected == null || current == null)
                return new[] { "上游指纹缺失：原=" + (expected ?? "未读取") + "；现=" + (current ?? "未读取") };
            var before = expected.Split('\n');
            var after = current.Split('\n');
            var differences = new List<string>();
            for (var index = 0; index < Math.Max(before.Length, after.Length); index++)
            {
                var oldLine = index < before.Length ? before[index] : null;
                var newLine = index < after.Length ? after[index] : null;
                if (string.Equals(oldLine, newLine, StringComparison.Ordinal)) continue;
                if (index >= 3 && TryCompareEndpoint(oldLine, newLine, differences)) continue;
                var label = index == 0 ? "总装路径" : index == 1 ? "引用配置" : index == 2 ? "场景" : "上游条目" + index;
                differences.Add(label + "：原=" + (oldLine ?? "无") + "；现=" + (newLine ?? "无"));
            }
            return differences.AsReadOnly();
        }

        private static bool TryCompareEndpoint(string before, string after, List<string> differences)
        {
            if (!TryReadEndpoint(before, out var oldKey, out var oldPoints) ||
                !TryReadEndpoint(after, out var newKey, out var newPoints) || oldKey != newKey)
                return false;
            if (oldPoints.Length != newPoints.Length)
            {
                differences.Add("固定端点 " + oldKey + " 点数变化：原=" + oldPoints.Length / 3 + "；现=" + newPoints.Length / 3);
                return true;
            }
            for (var index = 0; index < oldPoints.Length; index++)
            {
                var delta = newPoints[index] - oldPoints[index];
                if (Math.Abs(delta) <= UpstreamCoordinateNoiseMm) continue;
                differences.Add("固定端点 " + oldKey + " · 点" + (index / 3 + 1) + "." + "XYZ"[index % 3] +
                    "：原=" + oldPoints[index].ToString("R", CultureInfo.InvariantCulture) + " mm；现=" +
                    newPoints[index].ToString("R", CultureInfo.InvariantCulture) + " mm；差量=" +
                    delta.ToString("R", CultureInfo.InvariantCulture) + " mm；尾差容限=0.000001 mm");
            }
            return true;
        }

        private static bool TryReadEndpoint(string line, out string key, out double[] points)
        {
            key = null; points = null;
            if (line == null) return false;
            var separator = line.IndexOf(':');
            if (separator < 3 || (line[0] != 'A' && line[0] != 'B') ||
                (line[1] != 'A' && line[1] != 'B' && line[1] != 'C') ||
                !int.TryParse(line.Substring(2, separator - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var member) || member < 0)
                return false;
            var values = line.Substring(separator + 1).Split(',');
            if (values.Length < 6 || values.Length % 3 != 0) return false;
            var parsed = new double[values.Length];
            for (var index = 0; index < values.Length; index++)
                if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) ||
                    double.IsNaN(parsed[index]) || double.IsInfinity(parsed[index])) return false;
            key = line.Substring(0, separator); points = parsed;
            return true;
        }

        private static IEnumerable<string> WallSignature(FaceToWallUpstreamGeometry wall)
        {
            if (wall == null) yield break;
            Func<IEnumerable<double>, string> numbers = values => string.Join(",", values.Select(BridgeUpstreamField.FormatMillimetres));
            Func<GeometRi.Point3d, IEnumerable<double>> point = p => new[] { p.X, p.Y, p.Z };
            Func<GeometRi.Vector3d, IEnumerable<double>> vector = v => new[] { v.X, v.Y, v.Z };
            yield return "wall/reference=" + wall.PreviewReferenceComponentName;
            // 缺省不换相兼容旧指纹；显式换相必须使上游身份变化。
            if (wall.IsPhaseSwitched) yield return "wall/phase-switched=true";
            if (wall.WallInstallation != null && !wall.WallInstallation.IsLegacyRead)
                yield return "wall/installation=" + numbers(new[] { wall.WallInstallation.Top, wall.WallInstallation.Bottom,
                    wall.WallInstallation.Left, wall.WallInstallation.Right, wall.WallInstallation.EntryDistance });
            yield return "wall/coordinates=" + numbers(wall.Coordinates.Origin.Concat(wall.Coordinates.Along)
                .Concat(wall.Coordinates.Across).Concat(wall.Coordinates.Up));
            yield return "wall/parameters=" + wall.Position + "/" + numbers(new[] { (double)wall.RootCount, wall.ModelClearance, wall.OverlapLength,
                wall.Cabinet.BaseWidth, wall.Cabinet.OpeningHeight, wall.Cabinet.InsulatorWithBracketHeight });
            yield return "wall/opening=" + numbers(point(wall.Cabinet.Opening));
            if (wall.Cabinet.ElevatedOrdinaryJoint != null)
                yield return "wall/elevated-joint=" + numbers(point(wall.Cabinet.ElevatedOrdinaryJoint));
            foreach (var corner in wall.WallOpeningCorners.OrderBy(p => p.Y).ThenBy(p => p.Z))
                yield return "wall/corner=" + numbers(point(corner));
            foreach (var bar in wall.Cabinet.Extensions.Concat(wall.WallConductors).Select(b => b.Phase + "/" +
                numbers(b.Shape.PointList.SelectMany(point).Concat(point(b.TerminalStart)).Concat(point(b.TerminalEnd))
                      .Concat(new[] { b.Shape.BusBarWidth, b.Shape.BusBarThickness, b.Shape.BarBendingRadius, b.Shape.VerticalBendingRadius,
                          b.Shape.StartLapWidth, b.Shape.EndLapWidth, b.Shape.BendAllowance })) + "/" +
                  numbers(vector(b.Shape.StartBusbarVector).Concat(vector(b.Shape.WidthVector))) + "/" +
                  b.Shape.IsCenter + "/" + b.Shape.IsMilledBar + "/" + b.Shape.IsVerticalBend + "/" +
                  string.Join(",", b.Shape.PreviewBendPointKinds ?? new List<CKBusbarBridgePreview.BusbarPreviewBendPointKind_e>()))
                .OrderBy(v => v, StringComparer.Ordinal)) yield return "wall/bar=" + bar;
            foreach (var connection in wall.Cabinet.OrdinaryConnections.Select(c =>
                numbers(point(c.Center).Concat(point(c.SolidCenter)).Concat(vector(c.Normal)).Concat(new[] { c.Radius })))
                .OrderBy(v => v, StringComparer.Ordinal)) yield return "wall/connection=" + connection;
        }

        private static IEnumerable<string> SideSignature(SideToSideUpstreamGeometry side)
        {
            if (side == null) yield break;
            yield return "side/preview-reference=" + side.PreviewReferenceComponentName;
            // 模型尺寸以 0.1 mm 为精度；COM 重读的末位浮点抖动不应使草稿失效。
            Func<double, string> axis = value =>
                (Math.Abs(value) < .0000005d ? 0d : value).ToString("0.000000", CultureInfo.InvariantCulture);
            Func<IEnumerable<double>, string> numbers = values => string.Join(",", values.Select(BridgeUpstreamField.FormatMillimetres));
            Func<IEnumerable<double>, string> axes = values => string.Join(",", values.Select(axis));
            Func<GeometRi.Point3d, IEnumerable<double>> point = p => new[] { p.X, p.Y, p.Z };
            Func<GeometRi.Vector3d, IEnumerable<double>> direction = v => new[] { v.X, v.Y, v.Z };
            yield return "side/coordinates=" + numbers(side.Coordinates.Origin) + "/" + axes(side.Coordinates.Along
                .Concat(side.Coordinates.Across).Concat(side.Coordinates.Up));
            yield return "side/parameters=" + side.Copper.FixedPhaseChange + "/" + side.Position + "/" + numbers(new[] {
                side.Copper.RootCount, side.Copper.Width, side.Copper.Thickness, side.Copper.PhaseSpacing,
                side.Copper.Clearance, side.Copper.CabinetEccentricity, side.FrameInsulatorBodyHeight });
            foreach (var cabinet in new[] { side.Copper.A, side.Copper.B })
            {
                yield return "side/cabinet=" + cabinet.PhaseSequence + "/" + numbers(point(cabinet.Opening)
                    .Concat(new[] { cabinet.OpeningHeight, cabinet.BaseWidth, cabinet.InsulatorWithBracketHeight }));
                yield return "side/elevated-ordinary-joint=" +
                    (cabinet.ElevatedOrdinaryJoint == null ? "none" : numbers(point(cabinet.ElevatedOrdinaryJoint)));
                foreach (var bar in cabinet.Extensions.Select(bar => "side/extension=" + bar.Phase + "/" + numbers(bar.Shape.PointList.SelectMany(point)
                        .Concat(point(bar.TerminalStart)).Concat(point(bar.TerminalEnd))
                        .Concat(new[] { bar.Shape.BusBarWidth, bar.Shape.BusBarThickness })) + "/" +
                        axes(direction(bar.Shape.StartBusbarVector).Concat(direction(bar.Shape.WidthVector))))
                    .OrderBy(value => value, StringComparer.Ordinal))
                    yield return bar;
                foreach (var group in new[] { cabinet.OrdinaryConnections, cabinet.ElevatedConnections })
                {
                    yield return "side/connection-host";
                    foreach (var connection in group.Select(connection =>
                            numbers(point(connection.Center).Concat(point(connection.SolidCenter))
                                .Concat(new[] { connection.Radius })) + "/" + axes(direction(connection.Normal)))
                        .OrderBy(value => value, StringComparer.Ordinal))
                        yield return connection;
                }
            }
        }
    }

    public interface IBridgeNativePreviewBatch
    {
        void Show();
        void Clear();
    }

    public interface IBridgeNativePreviewGateway
    {
        void ValidateContext();
        IBridgeNativePreviewBatch Build(BridgeDraftPreviewSnapshot snapshot);
    }

    /// <summary>两条链路统一提交。失败清理保留引用供重试，不把临时显示当作SW事务。</summary>
    public sealed class BridgeNativePreviewCoordinator
    {
        private readonly IBridgeNativePreviewGateway _gateway;
        private readonly List<IBridgeNativePreviewBatch> _batches = new List<IBridgeNativePreviewBatch>();
        public long? DisplayedRevision { get; private set; }
        public bool HasBodies => _batches.Count > 0;
        public BridgeNativePreviewCoordinator(IBridgeNativePreviewGateway gateway) { _gateway = gateway; }

        public void Refresh(BridgeDraftPreviewSnapshot snapshot, Func<bool> isCurrent)
        {
            try
            {
                _gateway.ValidateContext();
                var candidate = _gateway.Build(snapshot) ?? throw new InvalidOperationException("未创建3D预览批次。");
                _batches.Add(candidate);
                _gateway.ValidateContext();
                if (!isCurrent()) throw new InvalidOperationException("草稿版本已改变，本次3D结果已取消。");
                foreach (var previous in _batches.Where(b => !ReferenceEquals(b, candidate)).ToArray())
                {
                    previous.Clear(); _batches.Remove(previous);
                }
                DisplayedRevision = null;
                candidate.Show();
                if (!isCurrent()) throw new InvalidOperationException("显示期间草稿版本已改变。");
                DisplayedRevision = snapshot.Revision;
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("草稿3D预览批次失败", error);
                try { Clear(); }
                catch (Exception cleanupError)
                {
                    FaceToWallPreviewTrace.Error("草稿3D预览补偿清理失败", cleanupError);
                    throw new AggregateException("3D预览失败且清理未完成，请重试清除。", error, cleanupError);
                }
                throw;
            }
        }

        public void Clear()
        {
            DisplayedRevision = null;
            var failures = new List<Exception>();
            foreach (var batch in _batches.ToArray())
            {
                try { batch.Clear(); _batches.Remove(batch); }
                catch (Exception error) { failures.Add(error); FaceToWallPreviewTrace.Error("3D批次清理失败", error); }
            }
            if (failures.Count > 0) throw new AggregateException("部分3D实体尚未清理，可重试清除。", failures);
        }

        // 文档已被SW销毁，实体随文档失效；不再调用失效COM对象。
        public void DocumentDestroyed() { _batches.Clear(); DisplayedRevision = null; }
    }
}
