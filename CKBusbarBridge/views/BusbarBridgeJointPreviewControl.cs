using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.views
{
    /// <summary>只画内存草稿，不创建SolidWorks预览实体。鼠标过程为临时图形，释放才提交。</summary>
    public sealed class BusbarBridgeJointPreviewControl : FrameworkElement
    {
        private readonly BusbarBridgeDraftPreviewSceneFactory _geometry = new BusbarBridgeDraftPreviewSceneFactory();
        private readonly BusbarBridgeInsulatorClearance _clearance = new BusbarBridgeInsulatorClearance();
        private readonly List<Tuple<Rect, string>> _hits = new List<Tuple<Rect, string>>();
        private readonly List<Tuple<Rect, string>> _issueHits = new List<Tuple<Rect, string>>();
        private readonly List<Tuple<Rect, BridgeDesignIssue>> _issueLocationHits = new List<Tuple<Rect, BridgeDesignIssue>>();
        private readonly List<Tuple<Rect, string>> _frameLabelHits = new List<Tuple<Rect, string>>();
        private readonly List<Tuple<Rect, string>> _hangerHits = new List<Tuple<Rect, string>>();
        private readonly Dictionary<string, Rect> _frameScreenBounds = new Dictionary<string, Rect>();
        private readonly List<Rect> _structureScreenBounds = new List<Rect>();
        private readonly Dictionary<string, Tuple<string, double, FormattedText>> _frameLabelText =
            new Dictionary<string, Tuple<string, double, FormattedText>>();
        private readonly ToolTip _issueToolTip;
        private readonly BridgeCopperRoundedProjection _roundedProjection = new BridgeCopperRoundedProjection();
        private readonly Dictionary<BridgeDesignRoute, Geometry> _topRounded = new Dictionary<BridgeDesignRoute, Geometry>();
        private readonly Dictionary<BridgeDesignRoute, Geometry> _sideRounded = new Dictionary<BridgeDesignRoute, Geometry>();
        private BridgeDesignSample _roundedSample;
        private long _roundedRevision = -1;
        private IReadOnlyList<BridgeDesignObject> _solidSource;
        private IReadOnlyList<BridgeDesignObject> _projectionPlan;
        private readonly Dictionary<string, IReadOnlyList<BridgePreviewSolid>> _nodeSolids = new Dictionary<string, IReadOnlyList<BridgePreviewSolid>>();
        private readonly Dictionary<string, IReadOnlyList<BridgePreviewSolid>> _bracketSolids = new Dictionary<string, IReadOnlyList<BridgePreviewSolid>>();
        private readonly Dictionary<IReadOnlyList<BridgePreviewSolid>, IReadOnlyList<ProjectedSolid>> _topSolids = new Dictionary<IReadOnlyList<BridgePreviewSolid>, IReadOnlyList<ProjectedSolid>>();
        private readonly Dictionary<IReadOnlyList<BridgePreviewSolid>, IReadOnlyList<ProjectedSolid>> _sideSolids = new Dictionary<IReadOnlyList<BridgePreviewSolid>, IReadOnlyList<ProjectedSolid>>();
        private readonly Dictionary<BridgePreviewMesh, Geometry> _topMeshes = new Dictionary<BridgePreviewMesh, Geometry>();
        private readonly Dictionary<BridgePreviewMesh, Geometry> _sideMeshes = new Dictionary<BridgePreviewMesh, Geometry>();
        private readonly Dictionary<BridgeDesignRoute, IReadOnlyList<IReadOnlyList<BridgePreviewSolid>>> _physicalSolids = new Dictionary<BridgeDesignRoute, IReadOnlyList<IReadOnlyList<BridgePreviewSolid>>>();
        private sealed class ProjectedSolid
        {
            internal Geometry Geometry { get; }
            internal Brush Fill { get; }
            internal ProjectedSolid(Geometry geometry, Brush fill) { Geometry = geometry; Fill = fill; }
        }
        private IReadOnlyList<BridgeDesignObject> _displaySource;
        private IReadOnlyList<BridgeDesignObject> _displayObjects;
        private Dictionary<DesignField, double> _displayDragValues;
        private BridgeDesignObject _displayDragObject;
        private bool _displaySameSource;
        private long _displayRevision = -1;
        private BusbarBridgeDesignWindowVM _vm;
        private double _scale = .15;
        private Vector _pan = new Vector(140, 180);
        private Point _mouseStart;
        private Vector _panStart;
        private BridgeDesignObject _dragObject;
        private Dictionary<DesignField, double> _dragValues;
        private bool _panning;
        private bool _captured;
        private bool _dragSide;
        private bool _lastSide;
        private bool? _sideViewOverride;

        // 双视图分别固定投影方向，单视图使用视图模型的方向。
        internal bool? SideViewOverride
        {
            get => _sideViewOverride;
            set
            {
                if (_sideViewOverride == value) return;
                if (_captured) CancelGesture();
                _sideViewOverride = value;
                _lastSide = SideView;
                if (IsLoaded) Fit(false);
            }
        }
        private bool SideView => _sideViewOverride ?? (_vm?.SideView ?? false);

        public static Color PreviewBackgroundColor => Color.FromRgb(248, 250, 252);
        private static readonly Brush PreviewCanvasBrush = CreatePreviewCanvasBrush();

        private static Brush CreatePreviewCanvasBrush()
        {
            var tile = new Rect(0, 0, 12, 12);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(PreviewBackgroundColor), null, new RectangleGeometry(tile)));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(222, 231, 241)), null,
                new EllipseGeometry(new Point(6, 6), 0.7, 0.7)));
            var brush = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile, Viewbox = tile, ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = tile, ViewportUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            return brush;
        }

        public BusbarBridgeJointPreviewControl()
        {
            Focusable = true; ClipToBounds = true;
            _issueToolTip = new ToolTip
            {
                PlacementTarget = this, Placement = PlacementMode.MousePoint,
                HorizontalOffset = 12, VerticalOffset = 16, StaysOpen = true, IsHitTestVisible = false,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }
            };
            Loaded += OnLoaded; Unloaded += OnUnloaded;
            DataContextChanged += (s, e) => { if (IsLoaded) Attach(DataContext as BusbarBridgeDesignWindowVM); };
            SizeChanged += (s, e) => { if (_vm != null && !_captured) Fit(false); };
        }
        private void OnLoaded(object sender, RoutedEventArgs e) { Attach(DataContext as BusbarBridgeDesignWindowVM); Fit(false); }
        private void OnUnloaded(object sender, RoutedEventArgs e) { CancelGesture(); Attach(null); }
        private void Attach(BusbarBridgeDesignWindowVM vm)
        {
            _issueToolTip.IsOpen = false; _issueHits.Clear(); _frameLabelHits.Clear(); Cursor = null;
            if (_vm != null) { _vm.ViewChanged -= Refresh; _vm.FitRequested -= FitAll; _vm.FitSelectionRequested -= FitSelected; }
            _vm = vm;
            _lastSide = SideView;
            _roundedSample = null; _topRounded.Clear(); _sideRounded.Clear();
            ClearSolidProjection(null);
            _displaySource = null; _displayObjects = null; _displayDragValues = null; _displayDragObject = null;
            if (_vm != null) { _vm.ViewChanged += Refresh; _vm.FitRequested += FitAll; _vm.FitSelectionRequested += FitSelected; }
            InvalidateVisual();
        }
        private void Refresh(object sender, EventArgs e)
        {
            if (_captured && SideView != _dragSide) CancelGesture();
            if (_lastSide != SideView) { _lastSide = SideView; Fit(false); }
            else InvalidateVisual();
        }
        private void FitAll(object sender, EventArgs e) => Fit(false);
        private void FitSelected(object sender, EventArgs e) => Fit(true);
        private Point Project(double[] p) => new Point(p[0], SideView ? -p[2] : p[1]);
        private Point Screen(Point p) => new Point(p.X * _scale + _pan.X, p.Y * _scale + _pan.Y);
        private IReadOnlyList<BridgeDesignObject> DisplayObjects()
        {
            if (_dragObject == null || _dragValues == null) return _vm.Objects;
            if (ReferenceEquals(_displaySource, _vm.Objects) && ReferenceEquals(_displayDragValues, _dragValues) &&
                ReferenceEquals(_displayDragObject, _dragObject) && _displaySameSource == _vm.SameSource &&
                _displayRevision == _vm.Editor.Revision) return _displayObjects;
            var insulatorDrag = _dragObject.Kind == DesignObjectKind.Insulator;
            var targets = _vm.Editor.Affected(_dragObject.Id, !insulatorDrag && _vm.SameSource);
            var targetIds = new HashSet<string>(targets.Select(target => target.Id), StringComparer.Ordinal);
            var proposed = _vm.Objects.Select(node =>
            {
                // PlaceIndependently已按安装位置避让；临时改名会使整架未动的绝缘子投影失效。
                if (!targetIds.Contains(node.Id)) return node;
                var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                foreach (var pair in _dragValues) values[pair.Key] = node.Kind == DesignObjectKind.Frame || node.IsGrouped
                    ? node.Value(pair.Key) + pair.Value - _dragObject.Value(pair.Key) : pair.Value;
                return node.WithValues(values);
            }).ToArray();
            // 架内定位的临时预览只更新当前组；全桥自动布置会让其他安装位跟着鼠标移动。
            var result = insulatorDrag
                ? new BridgePhaseChangeSupportPlanner(_vm.Editor.CurrentSample, proposed).Apply(proposed, targetIds)
                : _clearance.PlaceIndependently(_vm.Editor.CurrentSample, proposed, _vm.Editor.IsPositionLocked,
                    _vm.Editor.IsManuallyPositionedInsulator).Objects;
            _displaySource = _vm.Objects; _displayDragValues = _dragValues; _displayDragObject = _dragObject;
            _displaySameSource = _vm.SameSource; _displayRevision = _vm.Editor.Revision; _displayObjects = result;
            return result;
        }

        private Geometry RoundedProjection(BridgeDesignRoute route)
        {
            var sample = _vm.Editor.CurrentSample;
            if (!ReferenceEquals(_roundedSample, sample) || _roundedRevision != _vm.Editor.Revision)
            {
                _topRounded.Clear(); _sideRounded.Clear(); _roundedSample = sample; _roundedRevision = _vm.Editor.Revision;
            }
            var cache = SideView ? _sideRounded : _topRounded;
            if (!cache.TryGetValue(route, out var geometry))
            {
                geometry = _roundedProjection.Create(route, sample.Coordinates, SideView);
                if (geometry?.CanFreeze == true) geometry.Freeze();
                cache[route] = geometry;
            }
            return geometry;
        }

        private IReadOnlyList<BridgePreviewSolid> CachedSolids(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects, bool bracket)
        {
            if (!ReferenceEquals(_solidSource, objects))
                UseSolidProjection(objects);
            var cache = bracket ? _bracketSolids : _nodeSolids;
            if (!cache.TryGetValue(node.Id, out var solids))
            {
                solids = bracket ? _geometry.InsulatorBracketSolids(node, objects) : _geometry.Solids(node, objects);
                cache[node.Id] = solids;
            }
            return solids;
        }

        private void ClearSolidProjection(IReadOnlyList<BridgeDesignObject> objects)
        {
            _nodeSolids.Clear(); _bracketSolids.Clear(); _topSolids.Clear(); _sideSolids.Clear();
            _topMeshes.Clear(); _sideMeshes.Clear(); _physicalSolids.Clear(); _solidSource = objects;
            _frameLabelText.Clear();
            _projectionPlan = objects == null ? null : _vm.Objects;
        }

        private void UseSolidProjection(IReadOnlyList<BridgeDesignObject> objects)
        {
            if (_solidSource == null || !ReferenceEquals(_projectionPlan, _vm.Objects))
            {
                ClearSolidProjection(objects); return;
            }
            // 拖动预览只替换受影响对象；未变对象及其父级的投影继续复用，旧拖动位置及时释放。
            var previous = _solidSource.ToDictionary(o => o.Id);
            var current = objects.ToDictionary(o => o.Id);
            foreach (var id in _nodeSolids.Keys.Concat(_bracketSolids.Keys).Distinct().ToArray())
            {
                previous.TryGetValue(id, out var oldNode); current.TryGetValue(id, out var node);
                if (SameSolidInputs(oldNode, node, previous, current)) continue;
                foreach (var cache in new[] { _nodeSolids, _bracketSolids })
                    if (cache.TryGetValue(id, out var solids))
                    {
                        _topSolids.Remove(solids); _sideSolids.Remove(solids); cache.Remove(id);
                    }
            }
            _solidSource = objects;
        }

        private bool SameSolidInputs(BridgeDesignObject previousNode, BridgeDesignObject node,
            IDictionary<string, BridgeDesignObject> previous, IDictionary<string, BridgeDesignObject> current)
        {
            if (previousNode == null || !ReferenceEquals(previousNode, node)) return false;
            if (node.ParentId == null) return true;
            if (!previous.TryGetValue(node.ParentId, out var oldParent) || !current.TryGetValue(node.ParentId, out var parent) ||
                !ReferenceEquals(oldParent, parent)) return false;
            if (parent.FrameTemplateKey != "SideToSidePhaseChange029") return true;
            // 029侧向绝缘子的共用支架由整组成员确定，不能只检查当前绝缘子。
            return previous.Values.Where(o => o.ParentId == parent.Id && o.InsulatorGroupId == node.InsulatorGroupId)
                .OrderBy(o => o.Id, StringComparer.Ordinal).SequenceEqual(current.Values
                    .Where(o => o.ParentId == parent.Id && o.InsulatorGroupId == node.InsulatorGroupId).OrderBy(o => o.Id, StringComparer.Ordinal));
        }

        private void Fit(bool selected)
        {
            if (_vm == null || ActualWidth < 10 || ActualHeight < 10) return;
            if (selected && _vm.SelectedHanger is BridgeHangerPlacement hanger)
            {
                var points = HangerPoints(hanger);
                if (points.Length == 0 && hanger.Frame != null) points = Corners(hanger.Frame, _vm.Objects);
                FitCorners(points);
                return;
            }
            if (selected && _vm.LocatedIssue != null)
            {
                FitCorners(_vm.LocatedIssue.CopperCorners.Concat(_vm.LocatedIssue.MetalCorners)
                    .Select(p => Project(p.ToArray())).ToArray());
                return;
            }
            if (selected && _vm.SelectedCopperSection != null)
            {
                var section = _vm.SelectedCopperSection;
                var physical = _vm.Editor.CurrentSample.Routes.Where(r => r.CopperSectionId == section.Id).ToArray();
                if (physical.Length > 0)
                {
                    FitCorners(physical.SelectMany(r => r.PhysicalSolids).SelectMany(s => s).Select(Project).ToArray());
                    return;
                }
                var route = _vm.Editor.CurrentSample.Routes.Where(r => r.LSegmentStartIds.Count == 0)
                    .OrderBy(r => r.Phase == "B" ? 0 : 1).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
                var segment = route == null ? null : _geometry.CopperSegments(route, _vm.Objects).ElementAtOrDefault(section.Index - 1);
                var segmentCorners = segment?.Points.Select(point => Project(_geometry.WorldPosition(point, _vm.Objects))).ToArray();
                if (segmentCorners != null && segmentCorners.Length > 0) { FitCorners(segmentCorners); return; }
            }
            var nodes = selected && _vm.HasMultipleFrames ? _vm.Objects.Where(o => _vm.SelectedFrameIds.Contains(o.Id)).ToArray()
                : selected && _vm.SelectedObject != null ? new[] { _vm.SelectedObject } : _vm.Objects;
            var corners = nodes.SelectMany(n => Corners(n, _vm.Objects)).ToArray();
            FitCorners(corners);
        }

        private void FitCorners(Point[] corners)
        {
            if (corners.Length == 0) { InvalidateVisual(); return; }
            var minX = corners.Min(p => p.X) - 180; var maxX = corners.Max(p => p.X) + 180;
            var minY = corners.Min(p => p.Y) - 180; var maxY = corners.Max(p => p.Y) + 180;
            _scale = Math.Max(.00001, Math.Min(2, Math.Min((ActualWidth - 70) / (maxX - minX), (ActualHeight - 75) / (maxY - minY))));
            _pan = new Vector(ActualWidth / 2 - (minX + maxX) / 2 * _scale, ActualHeight / 2 - (minY + maxY) / 2 * _scale);
            InvalidateVisual();
        }

        private Point[] Corners(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (node.ModelMesh != null)
                return Enumerable.Range(0, node.ModelMesh.Vertices.Count / 3).Select(i =>
                    Project(new[] { node.ModelMesh.Vertices[i * 3], node.ModelMesh.Vertices[i * 3 + 1], node.ModelMesh.Vertices[i * 3 + 2] })).ToArray();
            return _geometry.Solids(node, objects).Concat(_geometry.InsulatorBracketSolids(node, objects))
                .SelectMany(s => s.Corners).Select(Project).ToArray();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); _hits.Clear(); _issueHits.Clear(); _issueLocationHits.Clear();
            _frameLabelHits.Clear(); _frameScreenBounds.Clear(); _structureScreenBounds.Clear();
            dc.DrawRectangle(PreviewCanvasBrush, null, new Rect(RenderSize));
            if (_vm == null) { _issueToolTip.IsOpen = false; return; }
            if (_vm.Editor.Sample != null)
                Text(dc, _vm.Objects.Any(n => n.Id == "base-b") ? "路径方向：左 B 柜出排 → 右 A 柜进排" : "路径方向：A 柜 → 墙洞", new Point(12, 10), Brushes.SlateGray, 11);
            if (_vm.Objects.Count == 0)
            {
                _issueToolTip.IsOpen = false;
                Text(dc, _vm.HasUpstreamReader ? "暂无模型预览，读取和规划状态见下方。" : "未载入方案，请选择示例并点击“载入示例”。",
                    new Point(25, 70), Brushes.SlateGray);
                return;
            }
            var objects = DisplayObjects();
            if (!ReferenceEquals(_solidSource, objects)) UseSolidProjection(objects);
            if (SideView && _vm.ShowReferences)
            {
                var cabinets = objects.Where(n => n.Kind == DesignObjectKind.Cabinet).ToArray();
                if (cabinets.Length > 0)
                {
                    var ground = Screen(new Point(0, 0)).Y;
                    var left = Screen(new Point(cabinets.Min(n => n.X - n.Length / 2) - 160, 0)).X;
                    var right = Screen(new Point(cabinets.Max(n => n.X + n.Length / 2) + 160, 0)).X;
                    dc.DrawLine(new Pen(Brushes.SlateGray, 1), new Point(left, ground), new Point(right, ground));
                }
            }
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.Wall))
                if (Visible(node)) DrawNode(dc, node, objects);
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.Cabinet || o.Kind == DesignObjectKind.Base || o.Kind == DesignObjectKind.Frame))
                if (Visible(node)) DrawNode(dc, node, objects);
            // 前视基架侧板是透视示意；实际套管位于墙洞连接处，不能被侧板填色吞掉。
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.WallBushing))
                if (Visible(node)) DrawNode(dc, node, objects);
            // 支架在三相绝缘子及铜排下方；整套共用显示、命中和选中状态，不增加独立编辑点。
            foreach (var node in objects.Where(o => o.IsInsulatorGroup || o.InsulatorPlacement?.BracketSpan > 0)
                .Where(o => o.InsulatorPlacement?.BracketSpan > 0 ? _vm.ShowInsulators : Visible(o)))
            {
                var bracket = CachedSolids(node, objects, true);
                if (bracket.Count == 0) continue;
                var bounds = DrawSolidSet(dc, bracket, node.Id == _vm.Editor.SelectedId);
                bounds.Inflate(4, 4);
                _hits.Add(Tuple.Create(bounds, node.Id));
            }
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.Insulator || o.Kind == DesignObjectKind.FixedInsulator))
                if (Visible(node)) DrawNode(dc, node, objects);
            var selectedCopperPaths = new List<Tuple<Point[], double>>();
            if (_vm.ShowBusbars && _vm.Editor.Sample != null)
            foreach (var route in _vm.Editor.CurrentSample.Routes.Where(r => _vm.PhaseFilter == "全部" || _vm.PhaseFilter == r.Phase))
            {
                if (route.PhysicalSolids.Count > 0)
                {
                    var rounded = RoundedProjection(route);
                    if (rounded != null)
                    {
                        var transform = new MatrixTransform(_scale, 0, 0, _scale, _pan.X, _pan.Y);
                        var selected = route.CopperSectionId != null && route.CopperSectionId == _vm.SelectedCopperSection?.Id;
                        // 缓存保留草稿坐标；视口变换不改写几何，描边仍保持原像素宽度。
                        dc.PushTransform(transform);
                        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(162, 91, 66)),
                            new Pen(selected ? Brushes.DodgerBlue : Brushes.DimGray, (selected ? 1.7 : .75) / _scale), rounded);
                        dc.Pop();
                        _hits.Add(Tuple.Create(transform.TransformBounds(rounded.Bounds), route.CopperSectionId ?? route.PointIds[0]));
                        continue;
                    }
                    if (!_physicalSolids.TryGetValue(route, out var physical))
                    {
                        physical = route.PhysicalSolids.Select(s => (IReadOnlyList<BridgePreviewSolid>)
                            Array.AsReadOnly(new[] { new BridgePreviewSolid("copper", s) })).ToArray();
                        _physicalSolids[route] = physical;
                    }
                    foreach (var solid in physical)
                    {
                        var selected = route.CopperSectionId != null && route.CopperSectionId == _vm.SelectedCopperSection?.Id;
                        var bounds = DrawSolidSet(dc, solid, selected);
                        if (!bounds.IsEmpty) _hits.Add(Tuple.Create(bounds, route.CopperSectionId ?? route.PointIds[0]));
                    }
                    continue;
                }
                var points = route.PointIds.Select(id => objects.FirstOrDefault(o => o.Id == id)).Where(o => o != null).ToArray();
                var routeSegments = _geometry.CopperSegments(route, objects);
                for (var segmentIndex = 0; segmentIndex < routeSegments.Count; segmentIndex++)
                {
                var segment = routeSegments[segmentIndex];
                var segmentPath = _geometry.CopperSegmentPath(segment, objects, _vm.Editor.Sample.Source.BusbarWidth,
                    _vm.Editor.Sample.Source.BusbarThickness, _vm.Editor.Sample.Parameters.OverlapLengthMm);
                var sectionId = !route.IsPhysicalGeometry && route.LSegmentStartIds.Count == 0 ? BridgeCopperSection.IdFor(segmentIndex + 1) : null;
                var sectionSelected = sectionId != null && _vm.SelectedCopperSection?.Id == sectionId;
                var screenPath = segmentPath.Select(point => Screen(Project(point))).ToArray();
                var sectionWidth = segment.IsPhysicalGeometry ? segment.SectionWidth : _vm.Editor.Sample.Source.BusbarWidth;
                var sectionThickness = segment.IsPhysicalGeometry ? segment.SectionThickness : _vm.Editor.Sample.Source.BusbarThickness;
                var width = Math.Max(2, (SideView ? sectionThickness : sectionWidth) * _scale);
                if (sectionSelected) selectedCopperPaths.Add(Tuple.Create(screenPath, width));
                for (var i = 1; i < segmentPath.Length; i++)
                {
                    var a = screenPath[i - 1];
                    var b = screenPath[i];
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(85, 50, 37)), width + 1.4), a, b);
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(162, 91, 66)), width), a, b);
                    // 桥内铜排命中段排；随后绘制的断点句柄仍拥有更高优先级。
                    var target = sectionId ?? (segment.Points.Last().IsEditable ? segment.Points.Last().Id : segment.Points[0].Id);
                    var steps = Math.Max(1, Math.Min(300, (int)((b - a).Length / 8)));
                    for (var j = 0; j <= steps; j++)
                    {
                        var p = a + (b - a) * (j / (double)steps);
                        _hits.Add(Tuple.Create(new Rect(p.X - 6, p.Y - 6, 12, 12), target));
                    }
                }
                }
                if (!SideView && points.Length > 0 && route.LSegmentStartIds.Count == 0)
                    Text(dc, route.Phase, Screen(Project(_geometry.WorldPosition(points[0], objects))) + new Vector(3, -18), PhaseBrush(route.Phase), 11);
            }
            foreach (var selectedCopperPath in selectedCopperPaths)
                DrawSelectedCopperOutline(dc, selectedCopperPath.Item1, selectedCopperPath.Item2);
            foreach (var node in objects.Where(o => o.Kind != DesignObjectKind.Cabinet && o.Kind != DesignObjectKind.Base && o.Kind != DesignObjectKind.Frame &&
                o.Kind != DesignObjectKind.Wall && o.Kind != DesignObjectKind.WallBushing &&
                o.Kind != DesignObjectKind.Insulator && o.Kind != DesignObjectKind.FixedInsulator))
                if (Visible(node)) DrawNode(dc, node, objects);
            DrawFrameLabels(dc, objects);
            DrawHangers(dc);
            DrawClearanceIssues(dc, objects);
            foreach (var issues in _vm.Issues.Where(i => i.TargetId != null).GroupBy(i => i.TargetId))
            {
                var target = objects.FirstOrDefault(o => o.Id == issues.Key);
                if (target == null || !Visible(target)) continue;
                var bounds = Text(dc, "!", Screen(Project(_geometry.WorldPosition(target, objects))) + new Vector(12, -18), Brushes.DarkOrange);
                bounds.Inflate(4, 4);
                // 问题命中区独立于对象选择；同一标记显示全部问题，不改变点击和拖动目标。
                _issueHits.Add(Tuple.Create(bounds, string.Join(Environment.NewLine,
                    issues.Select(i => i.LevelText + "：" + i.Message).Distinct())));
            }
            // 基架由常驻节号标签表达选中状态，其他对象继续显示原有说明。
            foreach (var selectedNode in objects.Where(o => o.Kind != DesignObjectKind.Frame &&
                o.Id == _vm.Editor.SelectedId).Where(Visible))
                Text(dc, selectedNode.DisplayName, Screen(Project(_geometry.WorldPosition(selectedNode, objects))) + new Vector(14, 12), Brushes.DodgerBlue, 12);
            UpdateIssueToolTip(Mouse.GetPosition(this));
        }

        private Point[] HangerPoints(BridgeHangerPlacement hanger)
        {
            var coordinates = _vm.Editor.CurrentSample?.Coordinates;
            if (hanger.Pose == null || coordinates == null) return Array.Empty<Point>();
            var halfWidth = (hanger.Pose.Width + 200.0) / 2.0;
            return new[]
            {
                Project(coordinates.ToDraft(hanger.Pose.Point(0, 0, -halfWidth))),
                Project(coordinates.ToDraft(hanger.Pose.Point(0, 0, halfWidth))),
                Project(coordinates.ToDraft(hanger.Pose.Point(0, hanger.Plan?.InstallationHeight ?? 0, 0)))
            };
        }

        private void DrawHangers(DrawingContext dc)
        {
            _hangerHits.Clear();
            var layout = _vm.Editor.HangerLayout;
            if (layout?.Settings.Count > 0 && _vm.Editor.TryHangerStart(out var zero))
            {
                var point = Screen(Project(zero));
                var angle = layout.Direction.Value * Math.PI / 180.0;
                var direction = new Vector(Math.Cos(angle), SideView ? 0 : Math.Sin(angle));
                if (direction.Length > 0.0)
                {
                    direction.Normalize();
                    var tip = point + direction * 36;
                    var normal = new Vector(-direction.Y, direction.X);
                    var pen = new Pen(Brushes.SteelBlue, 1.5);
                    dc.DrawLine(pen, point, tip);
                    dc.DrawLine(pen, tip, tip - direction * 6 + normal * 3);
                    dc.DrawLine(pen, tip, tip - direction * 6 - normal * 3);
                    Text(dc, "吊架 0", point + new Vector(2, -17), Brushes.SteelBlue, 11);
                }
            }
            foreach (var hanger in _vm.Editor.HangerPlacements)
            {
                var points = HangerPoints(hanger).Select(Screen).ToArray();
                if (points.Length == 0) continue;
                var brush = hanger.Id == _vm.SelectedHangerId ? Brushes.DodgerBlue : hanger.IsValid ? Brushes.SlateGray : Brushes.IndianRed;
                var pen = new Pen(brush, hanger.Id == _vm.SelectedHangerId ? 3.0 : 1.5);
                var bottom = new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
                dc.DrawLine(pen, points[0], points[1]);
                if (SideView) dc.DrawLine(pen, bottom, points[2]);
                dc.DrawEllipse(brush, null, bottom, 3, 3);
                var label = Text(dc, hanger.Name, points[2] + new Vector(5, -15), brush, 11);
                _hangerHits.Add(Tuple.Create(label, hanger.Id));
                var bounds = new Rect(points[0], points[1]);
                bounds.Union(points[2]);
                bounds.Inflate(5, 5);
                _hangerHits.Add(Tuple.Create(bounds, hanger.Id));
            }
        }

        private void DrawClearanceIssues(DrawingContext dc, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 仅投影校验器保留的局部几何，不在重绘或鼠标移动中重新执行净距计算。
            if (_captured) return;
            var index = 0;
            foreach (var issue in _vm.GenerationBlockingIssues.Where(i => i.Location != null))
            {
                index++;
                var location = issue.Location;
                var route = _vm.Editor.CurrentSample?.Routes.FirstOrDefault(r => r.Id == issue.TargetId);
                var metal = objects.FirstOrDefault(o => o.Id == location.RelatedObjectId);
                if (!_vm.ShowBusbars || (metal != null && !Visible(metal)) ||
                    (route != null && _vm.PhaseFilter != "全部" && _vm.PhaseFilter != route.Phase)) continue;
                var areas = new[] { location.CopperCorners, location.MetalCorners }
                    .Select(corners => Hull(corners.Select(p => Screen(Project(p.ToArray()))).ToArray())).ToArray();
                var centers = new List<Point>();
                foreach (var area in areas)
                {
                    if (area.Length == 0) continue;
                    centers.Add(new Point(area.Average(p => p.X), area.Average(p => p.Y)));
                    var pen = new Pen(Brushes.Crimson, 2.5);
                    if (area.Length >= 3)
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 220, 20, 60)), pen, Polygon(area));
                    else if (area.Length == 2) dc.DrawLine(pen, area[0], area[1]);
                }
                if (centers.Count == 0) continue;
                var anchor = centers[0];
                var section = route == null ? null : _vm.Editor.FindCopperSection(route.CopperSectionId);
                var label = index + ". " + (location.FirstLabel ?? (route == null ? "铜排" : route.Phase + "相" + (section?.Name ?? "铜排")))
                    + " ↔ " + (location.SecondLabel ?? metal?.DisplayName ?? "基架") + "\n" + location.DistanceText;
                var formatted = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"), 12, Brushes.DarkRed, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var width = Math.Min(ActualWidth - 16, formatted.Width + 16);
                var rect = new Rect(Math.Max(8, Math.Min(ActualWidth - width - 8, anchor.X + 24)),
                    32 + (index - 1) * 60, Math.Max(1, width), formatted.Height + 10);
                // 引线指向参与校验的两块包络，不把包络中心连线冒充最短距离尺寸线。
                foreach (var center in centers)
                    dc.DrawLine(new Pen(Brushes.Crimson, 1) { DashStyle = DashStyles.Dash },
                        new Point(rect.Left + rect.Width / 2, rect.Bottom), center);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Crimson, 1.5), rect, 4, 4);
                dc.DrawText(formatted, new Point(rect.Left + 8, rect.Top + 5));
                _issueHits.Add(Tuple.Create(rect, issue.Message));
                _issueLocationHits.Add(Tuple.Create(rect, issue));
            }
        }

        private bool Visible(BridgeDesignObject node)
        {
            return _vm.IsPreviewObjectVisible(node);
        }
        private void DrawNode(DrawingContext dc, BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 圆弯离散点不作为编辑句柄；固定断点仍可选择、定位，但不允许拖动。
            if (node.Kind == DesignObjectKind.FixedRoutePoint || node.IsSuppressedInsulator) return;
            var p = Screen(Project(_geometry.WorldPosition(node, objects)));
            var representativeId = node.IsGrouped ? _vm.Editor.JointRepresentative(node.Id)?.Id : node.Id;
            var selected = representativeId == _vm.Editor.SelectedId || (node.Kind == DesignObjectKind.Frame && _vm.SelectedFrameIds.Contains(node.Id));
            var outline = new Pen(selected ? Brushes.DodgerBlue : Brushes.SlateGray, selected ? 3 : 1);
            var shape = node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Cabinet || node.Kind == DesignObjectKind.Base ||
                node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator;
            Rect bounds;
            if (node.ModelMesh != null)
            {
                bounds = DrawModelMesh(dc, node, selected);
                bounds.Inflate(3, 3);
            }
            else if (shape)
            {
                bounds = DrawSolids(dc, node, objects, selected);
                if (node.Kind == DesignObjectKind.Cabinet)
                    Text(dc, node.Name, new Point(bounds.Left + 6, bounds.Bottom + 5), Brushes.DimGray, 11);
                bounds.Inflate(4, 4);
            }
            else
            {
                // 移除圆点外观后仍保留真实断点接缝的选择和拖动，不暴露折弯离散点。
                if (!_vm.ShowHandles && !node.IsJointGroup) return;
                bounds = new Rect(p.X - 9, p.Y - 9, 18, 18);
                var brush = PhaseBrush(node.Phase);
                var isJoint = node.Kind == DesignObjectKind.Lap || node.Kind == DesignObjectKind.Breakpoint || node.Kind == DesignObjectKind.FixedJoint;
                if (isJoint)
                {
                    // 断点即搭接界面；这里只标记接缝，桥头实体属于相邻铜排段，不额外画连接板。
                    var h = Math.Max(3, (SideView ? _vm.Editor.Sample.Source.BusbarThickness : _vm.Editor.Sample.Source.BusbarWidth) * _scale);
                    var top = p.Y - h / 2; var bottom = p.Y + h / 2;
                    if (node.IsJointGroup && _vm.Editor.Sample.Source.IsModelDerived)
                    {
                        // 截面线覆盖该相所有叠排，前视不只在叠排中心画一个排厚的小短线。
                        var section = _vm.Editor.CurrentSample.Routes.Where(r => r.Phase == node.Phase)
                            .SelectMany(r => r.PhysicalSolids)
                            .Where(s => s.Min(c => c[0]) <= node.X + .2d && s.Max(c => c[0]) >= node.X - .2d)
                            .SelectMany(s => s).Select(c => Screen(Project(c))).ToArray();
                        if (section.Length > 0) { top = section.Min(c => c.Y); bottom = section.Max(c => c.Y); }
                    }
                    dc.DrawLine(new Pen(selected ? Brushes.DodgerBlue : Brushes.DimGray, selected ? 2.5 : 1),
                        new Point(p.X, top), new Point(p.X, bottom));
                    bounds = new Rect(p.X - 6, Math.Min(top, p.Y - 7), 12, Math.Max(bottom, p.Y + 7) - Math.Min(top, p.Y - 7));
                }
                // 只使用同一套编辑点，选中时改变原点样式，不叠加或强制显示另一套选中点。
                if (_vm.ShowHandles)
                {
                    // 所有断点使用同一个中性圆点；固定权限由草稿控制，不通过形状或相色区分。
                    if (isJoint) dc.DrawEllipse(Brushes.White, new Pen(selected ? Brushes.DodgerBlue : Brushes.DarkSlateGray, 1.5), p, 4, 4);
                    else if (node.Kind == DesignObjectKind.FixedEnd) dc.DrawRectangle(brush, outline, new Rect(p.X - 4, p.Y - 4, 8, 8));
                    else dc.DrawEllipse(Brushes.White, new Pen(selected ? Brushes.DodgerBlue : brush, selected ? 3 : 1), p, 4, 4);
                }
            }
            if (!bounds.IsEmpty && (node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Base ||
                node.Kind == DesignObjectKind.Cabinet))
            {
                _structureScreenBounds.Add(bounds);
                if (node.Kind == DesignObjectKind.Frame) _frameScreenBounds[node.Id] = bounds;
            }
            _hits.Add(Tuple.Create(bounds, representativeId));
            if (selected && SideView && _scale > .4 && _vm.Editor.CurrentSample.Routes.Any(r => r.StraightLapStartIds.Contains(node.Id)))
            {
                Text(dc, "进基座铜排：直排头", p + new Vector(-210 * _scale, 42), Brushes.SaddleBrown, 12);
                Text(dc, "L排：直排头", p + new Vector(15, -38), Brushes.SaddleBrown, 12);
            }
        }
        private Rect DrawModelMesh(DrawingContext dc, BridgeDesignObject node, bool selected)
        {
            var cache = SideView ? _sideMeshes : _topMeshes;
            if (!cache.TryGetValue(node.ModelMesh, out var geometry))
            {
                geometry = new BridgeModelMeshProjection().Create(node.ModelMesh, SideView);
                geometry.Freeze(); cache[node.ModelMesh] = geometry;
            }
            var brush = selected ? Brushes.DodgerBlue : new SolidColorBrush(node.Kind == DesignObjectKind.FixedBusbar
                ? Color.FromRgb(162, 91, 66) : node.Kind == DesignObjectKind.Wall ? Color.FromArgb(80, 130, 134, 140) : Color.FromRgb(241, 239, 229));
            var transform = new MatrixTransform(_scale, 0, 0, _scale, _pan.X, _pan.Y);
            dc.PushTransform(transform);
            dc.DrawGeometry(brush, null, geometry);
            dc.Pop();
            return transform.TransformBounds(geometry.Bounds);
        }

        private Rect DrawSolids(DrawingContext dc, BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects, bool selected)
        {
            var result = DrawSolidSet(dc, CachedSolids(node, objects, false), selected, node);
            if ((node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Base) && SideView && result.Width > 35)
            {
                // 孔槽仅作外形示意，不输出孔位/数量制造数据。
                for (var col = 1; col <= 3; col++) for (var row = 1; row <= 2; row++)
                {
                    var x = result.Left + result.Width * col / 4; var y = result.Top + result.Height * (.1 + row * .1);
                    dc.DrawLine(new Pen(Brushes.DarkSlateGray, 1), new Point(x - 3, y), new Point(x + 3, y));
                }
            }
            return result;
        }
        private Rect DrawSolidSet(DrawingContext dc, IReadOnlyList<BridgePreviewSolid> solids, bool selected, BridgeDesignObject node = null)
        {
            var result = Rect.Empty;
            var transform = new MatrixTransform(_scale, 0, 0, _scale, _pan.X, _pan.Y);
            var pen = new Pen(selected ? Brushes.DodgerBlue : Brushes.DimGray, (selected ? 1.7 : .75) / _scale);
            dc.PushTransform(transform);
            foreach (var solid in SolidProjection(solids, node))
            {
                dc.DrawGeometry(solid.Fill, pen, solid.Geometry);
                result.Union(solid.Geometry.Bounds);
            }
            dc.Pop();
            return result.IsEmpty ? result : transform.TransformBounds(result);
        }

        private IReadOnlyList<ProjectedSolid> SolidProjection(IReadOnlyList<BridgePreviewSolid> solids, BridgeDesignObject node)
        {
            var cache = SideView ? _sideSolids : _topSolids;
            if (cache.TryGetValue(solids, out var cached)) return cached;
            IEnumerable<BridgePreviewSolid> ordered = solids;
            if (node != null)
                // 入口封板必须最后画，否则盖板/端部角钢会盖住偏心孔口。
                ordered = solids.Where(s => !s.IsHiddenIn2D).OrderBy(s => node.WallFramePreview != null &&
                    (s.Material == "seal" || s.Material == "seal-rim") ? 3 :
                    s.Material == "rim" ? 2 : SideView && s.Material == "floor" ? 1 : 0)
                    .ThenBy(s => SideView ? -s.Corners.Average(p => p[1]) : s.Corners.Average(p => p[2]));
            var items = ordered.ToArray();
            var result = new List<ProjectedSolid>();
            Geometry roundOutline = null;
            foreach (var solid in items.Where(s => s.Round != null))
            {
                var hull = Hull(solid.Corners.Select(Project).ToArray());
                if (hull.Length < 3) continue;
                var part = Polygon(hull);
                roundOutline = roundOutline == null ? (Geometry)part : Geometry.Combine(roundOutline, part, GeometryCombineMode.Union, null);
            }
            if (roundOutline != null)
            {
                // 合并只在投影首次使用时执行；视口变化不重算回转截段，也不改写缓存几何。
                roundOutline.Freeze();
                result.Add(new ProjectedSolid(roundOutline, MaterialBrush("insulator")));
            }
            foreach (var solid in items.Where(s => s.Round == null))
            {
                var hull = Hull(solid.Corners.Select(Project).ToArray());
                if (hull.Length < 3) continue;
                var geometry = Polygon(hull);
                result.Add(new ProjectedSolid(geometry, MaterialBrush(solid.Material)));
            }
            cached = result.AsReadOnly(); cache[solids] = cached; return cached;
        }

        private Brush MaterialBrush(string material)
        {
            var color = material == "pad" ? Color.FromRgb(54, 107, 157) : material == "copper" ? Color.FromRgb(162, 91, 66) : material == "cabinet" ? Color.FromRgb(192, 195, 198) : material == "insulator" ? Color.FromRgb(241, 239, 229) :
                material == "metal" || material == "rim" ? Color.FromRgb(181, 184, 184) : material == "bracket" ? Color.FromRgb(145, 146, 142) :
                material == "floor" ? Color.FromRgb(156, 158, 154) : Color.FromRgb(113, 111, 106);
            var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
        }
        private static Point[] Hull(Point[] points)
        {
            // 投影圆环的同列顶点可能相差1e-13像素。先归一化再排序，避免正反朝向因
            // 浮点噪声排出不同顺序、共线裁剪误删极值点，将圆柱画成斜楔。仅影响显示坐标。
            var sorted = points.Select(p => new Point(Math.Round(p.X, 6), Math.Round(p.Y, 6)))
                .Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            if (sorted.Length < 3) return sorted;
            var hull = new List<Point>();
            foreach (var p in sorted)
            {
                while (hull.Count >= 2 && Vector.CrossProduct(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 1]) <= .000001) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            var lower = hull.Count;
            foreach (var p in sorted.Reverse().Skip(1))
            {
                while (hull.Count > lower && Vector.CrossProduct(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 1]) <= .000001) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1); return hull.ToArray();
        }
        private StreamGeometry Polygon(Point[] points)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open()) { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            geometry.Freeze(); return geometry;
        }
        private void DrawSelectedCopperOutline(DrawingContext dc, Point[] points, double copperWidth)
        {
            if (points.Length < 2) return;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                context.PolyLineTo(points.Skip(1).ToArray(), true, false);
            }
            geometry.Freeze();
            var pen = new Pen(Brushes.DodgerBlue, copperWidth + 5)
            {
                StartLineCap = PenLineCap.Square,
                EndLineCap = PenLineCap.Square,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            var copperPen = new Pen(new SolidColorBrush(Color.FromRgb(162, 91, 66)), copperWidth)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
                LineJoin = PenLineJoin.Round
            };
            copperPen.Freeze();
            dc.DrawGeometry(null, copperPen, geometry);
        }
        private Brush PhaseBrush(string phase) => phase == "A" ? Brushes.DarkGoldenrod : phase == "B" ? Brushes.SeaGreen : phase == "C" ? Brushes.IndianRed : Brushes.SlateGray;
        private void DrawFrameLabels(DrawingContext dc, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (ActualWidth <= 20 || ActualHeight <= 70) return;
            var viewport = new Rect(8, 32, ActualWidth - 16, ActualHeight - 66);
            var occupied = new List<Rect>();
            var selectedIds = new HashSet<string>(_vm.SelectedFrameIds);
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame && Visible(o))
                .OrderBy(o => o.FramePositionIndex).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                if (!_frameScreenBounds.TryGetValue(frame.Id, out var projection) || !projection.IntersectsWith(viewport)) continue;
                var selected = selectedIds.Contains(frame.Id) || frame.Id == _vm.Editor.SelectedId;
                var text = FrameLabelText(frame);
                text.SetForegroundBrush(selected ? Brushes.DodgerBlue : Brushes.DarkSlateGray);
                var size = new Size(text.WidthIncludingTrailingWhitespace + 12, text.Height + 8);
                var bounds = PlaceFrameLabel(projection, size, viewport, occupied);
                var middle = new Point(projection.Left + projection.Width / 2, projection.Top + projection.Height / 2);
                var end = NearestLabelPoint(bounds, middle);
                var start = NearestLabelPoint(projection, end);
                var pen = new Pen(selected ? Brushes.DodgerBlue : Brushes.SlateGray, selected ? 1.5 : 1);
                if ((end - start).Length > 4) dc.DrawLine(pen, start, end);
                dc.DrawRoundedRectangle(selected ? Brushes.AliceBlue : Brushes.WhiteSmoke, pen, bounds, 3, 3);
                dc.DrawText(text, bounds.TopLeft + new Vector(6, 4));
                occupied.Add(bounds);
                // 独立命中区优先于铜排和绝缘子，并始终绑定物理实例ID。
                _frameLabelHits.Add(Tuple.Create(bounds, frame.Id));
            }
        }

        private FormattedText FrameLabelText(BridgeDesignObject frame)
        {
            var content = "第" + frame.FramePositionIndex.ToString(CultureInfo.InvariantCulture) + "节 · L=" +
                frame.Length.ToString("0.0", CultureInfo.InvariantCulture) + " mm";
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (!_frameLabelText.TryGetValue(frame.Id, out var cached) || cached.Item1 != content || Math.Abs(cached.Item2 - dpi) > .000001)
            {
                var text = new FormattedText(content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"), 11, Brushes.DarkSlateGray, dpi);
                cached = Tuple.Create(content, dpi, text); _frameLabelText[frame.Id] = cached;
            }
            return cached.Item3;
        }

        private Rect PlaceFrameLabel(Rect frame, Size size, Rect viewport, IReadOnlyList<Rect> occupied)
        {
            var best = Rect.Empty; var bestScore = double.PositiveInfinity;
            var horizontal = frame.Width >= frame.Height;
            for (var lane = 0; lane < Math.Min(24, _frameScreenBounds.Count + 4); lane++)
            {
                var top = new Point(frame.Left + (frame.Width - size.Width) / 2, frame.Top - size.Height - 10 - lane * (size.Height + 6));
                var bottom = new Point(top.X, frame.Bottom + 10 + lane * (size.Height + 6));
                var left = new Point(frame.Left - size.Width - 10 - lane * (size.Width + 6), frame.Top + (frame.Height - size.Height) / 2);
                var right = new Point(frame.Right + 10 + lane * (size.Width + 6), left.Y);
                foreach (var point in horizontal ? new[] { top, bottom, right, left } : new[] { right, left, top, bottom })
                {
                    var bounds = new Rect(new Point(Math.Max(viewport.Left, Math.Min(point.X, viewport.Right - size.Width)),
                        Math.Max(viewport.Top, Math.Min(point.Y, viewport.Bottom - size.Height))), size);
                    var padded = bounds; padded.Inflate(4, 4);
                    var labels = occupied.Sum(r => LabelOverlap(padded, r));
                    var structures = _structureScreenBounds.Sum(r => LabelOverlap(padded, r));
                    if (labels < .000001 && structures < .000001) return bounds;
                    // 极小视口仍保留全部节号；优先保护相邻标签的可读性。
                    var score = labels * 1000 + structures + (bounds.TopLeft - frame.TopLeft).Length * .001;
                    if (score < bestScore) { best = bounds; bestScore = score; }
                }
            }
            return best;
        }

        private double LabelOverlap(Rect left, Rect right)
        {
            var overlap = Rect.Intersect(left, right);
            return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        }

        private Point NearestLabelPoint(Rect bounds, Point point) => new Point(
            Math.Max(bounds.Left, Math.Min(point.X, bounds.Right)), Math.Max(bounds.Top, Math.Min(point.Y, bounds.Bottom)));

        private bool TrySelectFrameLabel(Point point, bool multiple)
        {
            var hit = _frameLabelHits.LastOrDefault(h => h.Item1.Contains(point));
            if (hit == null || _vm == null || _vm.HasPendingInput) return false;
            if (multiple) _vm.ToggleFrameSelection(hit.Item2);
            else _vm.LocateFrameParameters(hit.Item2);
            return true;
        }

        private BridgeDesignObject SelectPreviewObject(string id)
        {
            var hit = _vm.Editor.Find(id);
            var owner = _vm.DesignFrameFor(hit);
            if (owner == null)
            {
                _vm.Select(id);
                return _vm.SelectedObject;
            }
            _vm.LocateFrameParameters(owner.Id);
            return hit.Kind == DesignObjectKind.Insulator ? _vm.Editor.JointRepresentative(id) : _vm.SelectedObject;
        }

        private bool SameSourceDrag(BridgeDesignObject node) => node?.Kind != DesignObjectKind.Insulator && _vm.SameSource;

        private Rect Text(DrawingContext dc, string text, Point p, Brush brush, double size = 12)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, p);
            return new Rect(p, new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height));
        }
        private void UpdateIssueToolTip(Point position)
        {
            var messages = _issueHits.Where(h => h.Item1.Contains(position)).Select(h => h.Item2).Distinct().ToArray();
            if (_captured || !IsMouseOver || messages.Length == 0) { _issueToolTip.IsOpen = false; return; }
            ((TextBlock)_issueToolTip.Content).Text = string.Join(Environment.NewLine, messages);
            _issueToolTip.IsOpen = true;
        }
        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _issueToolTip.IsOpen = false;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e); if (_captured || _vm == null) return;
            var p = e.GetPosition(this); var ratio = e.Delta > 0 ? 1.15 : 1 / 1.15;
            var next = Math.Max(.00001, Math.Min(4, _scale * ratio)); ratio = next / _scale;
            _pan = new Vector(p.X - (p.X - _pan.X) * ratio, p.Y - (p.Y - _pan.Y) * ratio); _scale = next;
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); _issueToolTip.IsOpen = false; if (_vm == null) return;
            Focus(); if (_vm.HasPendingInput) return;
            var p = e.GetPosition(this);
            var issueHit = _issueLocationHits.LastOrDefault(h => h.Item1.Contains(p));
            if (issueHit != null) { _vm.Locate(issueHit.Item2); e.Handled = true; return; }
            var hangerHit = (Keyboard.Modifiers & ModifierKeys.Control) == 0
                ? _hangerHits.LastOrDefault(candidate => candidate.Item1.Contains(p)) : null;
            if (hangerHit != null) { _vm.SelectedHangerId = hangerHit.Item2; e.Handled = true; return; }
            if (TrySelectFrameLabel(p, (Keyboard.Modifiers & ModifierKeys.Control) != 0)) { e.Handled = true; return; }
            var hit = _hits.LastOrDefault(h => h.Item1.Contains(p));
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl专选基架，避免铜排或绝缘子命中层挡住基架多选。
                var frameHit = _hits.LastOrDefault(h => h.Item1.Contains(p) && _vm.Editor.Find(h.Item2)?.Kind == DesignObjectKind.Frame);
                if (frameHit != null) _vm.ToggleFrameSelection(frameHit.Item2);
                e.Handled = true; return;
            }
            _mouseStart = p; _panStart = _pan; _dragSide = SideView;
            if (hit != null)
            {
                if (_vm.Editor.FindCopperSection(hit.Item2) != null)
                {
                    _vm.SelectCopperSection(hit.Item2);
                    e.Handled = true; return;
                }
                var node = SelectPreviewObject(hit.Item2);
                if (e.ClickCount == 2) { e.Handled = true; return; }
                if (node == null || !_vm.Editor.CanDragObject(node.Id, SameSourceDrag(node))) { e.Handled = true; return; }
                _dragObject = node; _vm.SetDragging(true);
            }
            else _panning = true;
            _captured = CaptureMouse(); e.Handled = true;
            if (!_captured) CancelGesture();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); UpdateIssueToolTip(e.GetPosition(this));
            if (!_captured) Cursor = _frameLabelHits.Any(h => h.Item1.Contains(e.GetPosition(this))) ||
                _issueLocationHits.Any(h => h.Item1.Contains(e.GetPosition(this))) ? Cursors.Hand : null;
            if (!_captured || _vm == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { CancelGesture(); return; }
            var delta = e.GetPosition(this) - _mouseStart;
            if (_panning) _pan = _panStart + delta;
            else if (_dragObject != null && delta.Length >= 3)
            {
                var dx = delta.X / _scale; var dy = _dragSide ? 0 : delta.Y / _scale; var dz = _dragSide ? -delta.Y / _scale : 0;
                // 俯视绝缘子拖动只响应鼠标水平位移，避免偏移基架把竖向鼠标抖动换算成定位变化。
                if (!_dragSide && _dragObject.Kind == DesignObjectKind.Insulator) dy = 0;
                var parent = _vm.Objects.FirstOrDefault(o => o.Id == _dragObject.ParentId);
                if (parent != null)
                {
                    var a = parent.ModelDirection * Math.PI / 180;
                    var localX = dx * Math.Cos(a) + dy * Math.Sin(a);
                    dy = -dx * Math.Sin(a) + dy * Math.Cos(a); dx = localX;
                }
                _dragValues = _vm.Editor.ConstrainDragValues(_dragObject.Id, new Dictionary<DesignField, double>
                { [DesignField.X] = _dragObject.X + dx, [DesignField.Y] = _dragObject.Y + dy, [DesignField.Z] = _dragObject.Z + dz }, SameSourceDrag(_dragObject));
            }
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!_captured) return;
            var id = _dragObject?.Id; var values = _dragValues;
            EndGesture(); if (id != null && values != null) _vm.CommitDrag(id, values);
            e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (_captured) CancelGesture(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (e.Key == Key.Escape && _captured) { CancelGesture(); e.Handled = true; }
        }
        private void CancelGesture() { if (_panning) _pan = _panStart; EndGesture(); }
        private void EndGesture()
        {
            _captured = false; _panning = false; _dragObject = null; _dragValues = null;
            _vm?.SetDragging(false); if (IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual();
        }
    }
}
