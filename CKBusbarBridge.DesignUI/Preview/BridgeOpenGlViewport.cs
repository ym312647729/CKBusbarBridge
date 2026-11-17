using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal struct PreviewPoint3
    {
        internal float X { get; }
        internal float Y { get; }
        internal float Z { get; }
        internal PreviewPoint3(double x, double y, double z) { X = (float)x; Y = (float)y; Z = (float)z; }
    }

    internal sealed class BridgePreviewAppearance
    {
        internal static BridgePreviewAppearance Shared { get; } = new BridgePreviewAppearance();
        internal float Transparency { get; set; } = .15f;
        internal float TargetFramesPerSecond { get; set; } = 60f;
        internal int ObjectTriangleBudget { get; set; } = 80000;
        internal int SceneTriangleBudget { get; set; } = 700000;
        internal float ChordToleranceMm { get; set; } = .3f;
        internal int CircleSegments { get; set; } = 24;
        internal float InteractionFrameBudgetMs { get; set; } = 1000f / 30f;
    }

    // Pure managed frozen frames enter here. The native host owns the SW subscription and redraw requests.
    internal sealed partial class BridgeOpenGlViewport : IDisposable
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private BridgeMeshFrame _frame;
        private BridgeMeshPicker _picker;
        private float[] _inverseViewProjection;
        private BridgePreviewDimension[] _dimensions = Array.Empty<BridgePreviewDimension>();
        private Hit[] _hits = Array.Empty<Hit>();
        private bool _stop;
        private bool _leftPressed;
        private long _projectionRevision;
        private readonly System.Diagnostics.Stopwatch _frameWatch = new System.Diagnostics.Stopwatch();
        private readonly DispatcherTimer _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private bool _interaction, _idleQueued;
        private float _lastFrameMilliseconds;
        internal event Action IdleFrameRequested;
        private readonly Dictionary<IntPtr, ContextResources> _contexts = new Dictionary<IntPtr, ContextResources>();
        private readonly float[] _model = new float[16], _projection = new float[16];
        private readonly float[] _nativeProjection = new float[16];
        private readonly int[] _viewport = new int[4];
        internal BridgePreviewAppearance Appearance => BridgePreviewAppearance.Shared;
        internal event Action<BridgeDimensionEditRequest> EditRequested;
        internal bool IsDisposed { get; private set; }
        internal IntPtr WindowHandle => _messages.Handle;
        private bool _staleFrame;
        internal bool IsStale { get => _staleFrame || _viewModel?.PlanningBusy == true || _viewModel?.NativePreviewBusy == true || _viewModel?.IsDraftDocumentActive == false;
            set { _staleFrame = value; if (value) ResetDimensionHover(); } }
        internal bool IsPreviewOnly { get; set; }
        internal BridgeMeshFrame DisplayFrame => _frame;

        internal BridgeOpenGlViewport(IntPtr window)
        {
            // SW owns its HWND. A NativeWindow subclass receives messages without replacing the host window.
            _messages = new ViewportMessages(window, HandleMessage);
            _idleTimer.Tick += IdleFrame;
            _redrawTimer.Tick += RedrawScheduled;
            _orientationTimer.Tick += OrientationSettled;
            _dimensionHoverTimer.Tick += DimensionHoverElapsed;
        }
        private readonly ViewportMessages _messages;
        internal void Install(BridgeMeshFrame frame)
        {
            _dispatcher.VerifyAccess();
            if (!ReferenceEquals(_frame, frame)) _picker = new BridgeMeshPicker(frame.InteractionInstances);
            _frame = frame; _hits = Array.Empty<Hit>(); ResetDimensionHover(); RefreshVisibleIdentities();
            if (_viewModel != null) _viewModel.UpdateViewportReferences(frame.Instances);
        }
        internal void Dimensions(IEnumerable<BridgePreviewDimension> dimensions)
        { _dispatcher.VerifyAccess(); _dimensions = dimensions.ToArray(); _hits = Array.Empty<Hit>(); ResetDimensionHover(); }
        private string _focusedCabinetParameter;
        internal void HighlightCabinetParameter(string path)
        {
            if (string.Equals(_focusedCabinetParameter, path, StringComparison.Ordinal)) return;
            _focusedCabinetParameter = path; RequestRedraw();
        }
        internal void Clear() { _dispatcher.VerifyAccess(); _frame = null; _picker = null; _dimensions = Array.Empty<BridgePreviewDimension>(); _hits = Array.Empty<Hit>(); ResetDimensionHover(); }

        // Invoked only inside SW BufferSwapNotify, using its current GL matrices/context.
        internal int Draw(BridgeViewportDisplayStyle displayStyle = BridgeViewportDisplayStyle.ShadedWithEdges)
        {
            var context = Gl.wglGetCurrentContext(); if (context == IntPtr.Zero) return 0;
            if (!_contexts.TryGetValue(context, out var resources)) _contexts.Add(context, resources = new ContextResources());
            if (_stop) { resources.Delete(); _contexts.Remove(context); IsDisposed = _contexts.Count == 0; return 0; }
            var frame = _frame; if (frame == null) return 0;
            _displayStyle = displayStyle;
            _frameWatch.Restart();
            Gl.glPushAttrib(0x000fffff); Gl.glMatrixMode(0x1700); Gl.glPushMatrix(); Gl.glMatrixMode(0x1701); Gl.glPushMatrix();
            try
            {
                var previousModel = (float[])_model.Clone(); var previousProjection = (float[])_nativeProjection.Clone(); var previousViewport = (int[])_viewport.Clone();
                Gl.glGetFloatv(0x0ba6, _model); Gl.glGetFloatv(0x0ba7, _projection); Gl.glGetIntegerv(0x0ba2, _viewport);
                if (!previousModel.SequenceEqual(_model) || !previousProjection.SequenceEqual(_projection) || !previousViewport.SequenceEqual(_viewport))
                {
                    _projectionRevision++;
                    ResetDimensionHover();
                    _interaction |= _lastFrameMilliseconds > Appearance.InteractionFrameBudgetMs;
                    if (!_idleQueued)
                    {
                        _idleQueued = true; _dispatcher.BeginInvoke(new Action(() => { _idleQueued = false;
                            if (_stop) return; _idleTimer.Stop(); _idleTimer.Start(); }));
                    }
                }
                Array.Copy(_projection, _nativeProjection, 16);
                ExpandPreviewDepth(frame);
                _inverseViewProjection = ViewportMatrix.Inverse(ViewportMatrix.Multiply(_projection, _model));
                Gl.glLoadMatrixf(_projection);
                Gl.glMatrixMode(0x1700); Gl.glEnable(0x0b71); Gl.glDepthMask(1); Gl.glDisable(0x0b44);
                Gl.glPolygonMode(0x0408, 0x1b02);
                Gl.glEnable(0x0be2); Gl.glBlendFunc(0x0302, 0x0303);
                Gl.glEnable(0x0b50); Gl.glEnable(0x4000); Gl.glEnable(0x0ba1); Gl.glEnable(0x0b57);
                Gl.glColorMaterial(0x0408, 0x1602); Gl.glLightfv(0x4000, 0x1203, new[] { .4f, .8f, 1f, 0f });
                // All cached positions and installation translations are millimetres. This is the only m/mm boundary.
                Gl.glScalef(.001f, .001f, .001f);
                var instances = _interaction ? frame.InteractionInstances : frame.Instances;
                PrepareDisplayDepth(resources, instances);
                foreach (var instance in instances)
                    if (IsInstanceVisible(instance) && BridgeViewportAppearance.Opacity(instance, _viewModel?.SelectedTab) == 1f)
                        DrawInstance(resources, instance, 1f);
                // Transparent geometry never writes depth, so bases/environment do not hide copper behind them.
                Gl.glDepthMask(0);
                if (!ReferenceEquals(_transparentSource, instances) || _transparentViewRevision != _projectionRevision || _transparentTab != _viewModel?.SelectedTab)
                {
                    _transparentSource = instances; _transparentViewRevision = _projectionRevision; _transparentTab = _viewModel?.SelectedTab;
                    _transparentInstances = instances.Where(instance => BridgeViewportAppearance.Opacity(instance, _transparentTab) < 1f)
                        .OrderBy(instance => ViewportMatrix.Transform(_model, ViewportMatrix.Transform(instance.Transform,
                            (instance.Shape.PickingIndex.Bounds.Minimum + instance.Shape.PickingIndex.Bounds.Maximum) * .5f) * .001f).Z).ToArray();
                }
                foreach (var instance in _transparentInstances)
                    if (IsInstanceVisible(instance)) DrawInstance(resources, instance, BridgeViewportAppearance.Opacity(instance, _viewModel?.SelectedTab));
                Gl.glDepthMask(1);
                DrawDimensions(resources, frame);
                if (!IsPreviewOnly) DrawMarkers(resources);
            }
            finally
            {
                Gl.glMatrixMode(0x1701); Gl.glPopMatrix(); Gl.glMatrixMode(0x1700); Gl.glPopMatrix(); Gl.glPopAttrib();
                _frameWatch.Stop(); _lastFrameMilliseconds = (float)_frameWatch.Elapsed.TotalMilliseconds;
            }
            return 0;
        }
        private void IdleFrame(object sender, EventArgs args)
        { _idleTimer.Stop(); if (_stop || !_interaction) return; _interaction = false; IdleFrameRequested?.Invoke(); }

        private static void DrawFaces(float[] vertices, int[] indices, int stride, float[] normals = null)
        {
            if (indices.Length == 0) return;
            Gl.glBegin(stride == 4 ? 7u : stride == 3 ? 4u : 1u);
            for (var offset = 0; offset < indices.Length; offset += stride)
            {
                if (normals != null)
                { var normal = offset / stride * 3;
                    if (normals[normal] == 0f && normals[normal + 1] == 0f && normals[normal + 2] == 0f) continue;
                    Gl.glNormal3f(normals[normal], normals[normal + 1], normals[normal + 2]); }
                for (var corner = 0; corner < stride; corner++)
                { var index = indices[offset + corner]; Gl.glVertex3f(vertices[3 * index], vertices[3 * index + 1], vertices[3 * index + 2]); }
            }
            Gl.glEnd();
        }
        private IReadOnlyList<BridgeMeshInstance> _transparentSource;
        private BridgeMeshInstance[] _transparentInstances = Array.Empty<BridgeMeshInstance>();
        private long _transparentViewRevision = -1;
        private int? _transparentTab;
        private void DrawInstance(ContextResources resources, BridgeMeshInstance instance, float opacity)
        {
            Gl.glPushMatrix(); ApplyDragTransform(instance); Gl.glMultMatrixf(instance.Transform);
            try
            {
                if (instance.AppearanceRole == "cabinet") Gl.glDisable(0x0b50); else Gl.glEnable(0x0b50);
                Color(instance, opacity);
                var mesh = resources.Mesh(instance);
                if (!instance.OutlineOnly && DrawSurface) Gl.glCallList(mesh);
                Gl.glDisable(0x0b50);
                DrawHiddenEdges(mesh, opacity);
                if (BridgeViewportAppearance.IsEnvironment(instance.AppearanceRole)) { Gl.glColor4f(.8f, .8f, .8f, .5f); Gl.glLineWidth(1f); }
                else if (IsSelected(instance)) { Gl.glColor4f(1f, .55f, .05f, opacity); Gl.glLineWidth(2f); }
                else { Gl.glColor4f(.18f, .18f, .18f, opacity); Gl.glLineWidth(1f); }
                if (DrawEdges || IsSelected(instance) || instance.OutlineOnly) Gl.glCallList(mesh + 1);
                Gl.glEnable(0x0b50);
            }
            finally { Gl.glPopMatrix(); }
        }
        private void Color(BridgeMeshInstance item, float alpha)
        {
            if (item.AppearanceRole == "cabinet") { Gl.glColor4f(1f, 1f, 1f, 1f); return; }
            if (BridgeViewportAppearance.IsEnvironment(item.AppearanceRole)) { Gl.glColor4f(.8f, .8f, .8f, .5f); return; }
            if (IsStale) Gl.glColor4f(.48f, .48f, .48f, alpha);
            else if (IsSelected(item)) Gl.glColor4f(1f, .66f, .18f, alpha);
            else if (item.Phase == "A") Gl.glColor4f(.95f, .78f, .12f, alpha);
            else if (item.Phase == "B") Gl.glColor4f(.19f, .65f, .27f, alpha);
            else if (item.Phase == "C") Gl.glColor4f(.84f, .19f, .15f, alpha);
            else if (item.Category == "copper") Gl.glColor4f(.72f, .43f, .23f, alpha);
            else if (item.Category == "insulator") Gl.glColor4f(.56f, .25f, .17f, alpha);
            else Gl.glColor4f(.66f, .69f, .72f, alpha);
        }
        private ScreenPoint Project(PreviewPoint3 point)
        {
            var input = new[] { point.X, point.Y, point.Z, 1f }; var eye = new float[4]; var clip = new float[4];
            for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++) eye[row] += _model[col * 4 + row] * input[col];
            for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++) clip[row] += _projection[col * 4 + row] * eye[col];
            if (clip[3] <= 0f) return new ScreenPoint { Visible = false };
            return new ScreenPoint { X = (clip[0] / clip[3] + 1f) * _viewport[2] / 2f,
                Y = (clip[1] / clip[3] + 1f) * _viewport[3] / 2f, Z = clip[2] / clip[3], Visible = clip[2] >= -clip[3] && clip[2] <= clip[3] };
        }
        private void ExpandPreviewDepth(BridgeMeshFrame frame)
        {
            // An empty SW document's clipping range contains no physical model. Extend only our saved projection's Z row.
            // X/Y, perspective and the model transform remain native; mesh, text and hit testing share this projection.
            var minimum = float.PositiveInfinity; var maximum = float.NegativeInfinity;
            foreach (var x in new[] { frame.Minimum[0], frame.Maximum[0] })
                foreach (var y in new[] { frame.Minimum[1], frame.Maximum[1] })
                    foreach (var z in new[] { frame.Minimum[2], frame.Maximum[2] })
                    {
                        var projected = Project(new PreviewPoint3(x / 1000f, y / 1000f, z / 1000f));
                        minimum = Math.Min(minimum, projected.Z); maximum = Math.Max(maximum, projected.Z);
                    }
            if (float.IsInfinity(minimum) || float.IsInfinity(maximum) || float.IsNaN(minimum) || float.IsNaN(maximum)) return;
            if (minimum >= -1f && maximum <= 1f) return;
            var span = maximum - minimum;
            if (!(span > 0f)) span = 1f;
            var scale = 1.8f / span; var offset = -(maximum + minimum) * .5f * scale;
            for (var column = 0; column < 4; column++) _projection[column * 4 + 2] = scale * _projection[column * 4 + 2] + offset * _projection[column * 4 + 3];
        }
        private void DrawDimensions(ContextResources resources, BridgeMeshFrame frame)
        {
            Gl.glDisable(0x0b71); Gl.glDisable(0x0b50); Gl.glColor4f(.12f, .12f, .12f, 1f);
            Gl.glMatrixMode(0x1701); Gl.glLoadIdentity(); Gl.glOrtho(0, _viewport[2], 0, _viewport[3], -1, 1);
            Gl.glMatrixMode(0x1700); Gl.glLoadIdentity();
            var hits = new List<Hit>(); resources.EnsureFont();
            var labels = new List<ManualDrawAnnotationLabel>();
            var bodies = new Dictionary<string, System.Windows.Rect>(StringComparer.Ordinal);
            foreach (var instance in frame.Instances.Where(instance => IsInstanceVisible(instance) &&
                (instance.Category == "frame" || instance.Category == "base" || instance.Category == "raisedBase")))
            {
                var body = System.Windows.Rect.Empty; var bounds = instance.Shape.PickingIndex.Bounds;
                foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
                    foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
                        foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
                        {
                            var world = ViewportMatrix.Transform(instance.Transform, new ViewportVector(x, y, z));
                            var point = Project(new PreviewPoint3(world.X / 1000f, world.Y / 1000f, world.Z / 1000f));
                            if (point.Visible) body.Union(new System.Windows.Point(point.X, point.Y));
                        }
                if (body.IsEmpty) continue;
                if (bodies.TryGetValue(instance.DesignObjectId, out var previous)) body.Union(previous);
                bodies[instance.DesignObjectId] = body;
            }
            foreach (var dimension in _dimensions)
            {
                if (!IsDimensionVisible(dimension)) continue;
                bool focused = !IsPreviewOnly && (dimension.MatchesField(_viewModel?.FocusedDimensionField) ||
                    dimension.ParameterPath != null && dimension.ParameterPath == _focusedCabinetParameter);
                if (dimension.IsReadOnly) Gl.glColor4f(.45f, .45f, .45f, 1f); else Gl.glColor4f(.12f, .12f, .12f, 1f);
                var edges = dimension.Edges.Select(edge => new { Edge = edge, Points = edge.Points.Select(Project).ToArray() }).Where(edge => edge.Points.All(p => p.Visible)).ToArray();
                if (edges.Length == 0) continue;
                var selected = edges.OrderBy(edge => edge.Points.Average(p => p.Z)).First();
                // For a bent station path, keep the original polyline and place text on its longest visible straight segment.
                var segment = Enumerable.Range(1, selected.Points.Length - 1).OrderByDescending(index =>
                    Square(selected.Points[index].X - selected.Points[index - 1].X) + Square(selected.Points[index].Y - selected.Points[index - 1].Y)).First();
                var a = selected.Points[segment - 1]; var b = selected.Points[segment];
                var start = selected.Edge.Points[segment - 1]; var end = selected.Edge.Points[segment];
                var anchor = new PreviewPoint3((start.X + end.X) * .5f, (start.Y + end.Y) * .5f, (start.Z + end.Z) * .5f);
                var projectedAnchor = Project(anchor);
                var tangentLength = (float)Math.Sqrt(Square(b.X - a.X) + Square(b.Y - a.Y));
                if (tangentLength < 1f)
                {
                    if (!focused && dimension.ParameterPath != "General.CabinetEccentricity" && dimension.ParameterPath != "General.WallCabinetEccentricity") continue;
                    // Zero offset (or an end-on view) still needs an editable value.
                    // Draw a leader to the real mate datum, without inventing a model dimension line.
                    var callout = new ManualDrawAnnotationLabel { OriginX = a.X + 16f, OriginY = a.Y + 16f,
                        TangentX = 1f, TangentY = 0f, Width = resources.Width(dimension.Text) };
                    callout = ManualDrawAnnotationLayout.Avoid(callout, bodies.TryGetValue(dimension.ObjectId, out var offsetBody) ? offsetBody : System.Windows.Rect.Empty,
                        labels, _viewport[2], _viewport[3]);
                    Line(a.X, a.Y, callout.OriginX, callout.OriginY); labels.Add(callout);
                    DrawDimensionText(resources, dimension.Text, callout, focused);
                    hits.Add(new Hit { Dimension = dimension, Label = callout, SessionId = frame.SessionId, Revision = frame.Revision, ProjectionRevision = _projectionRevision });
                    continue;
                }
                var normalX = -(b.Y - a.Y) / tangentLength; var normalY = (b.X - a.X) / tangentLength;
                var scaleSquared = 0f;
                foreach (var offset in new[] { new PreviewPoint3(anchor.X + .001f, anchor.Y, anchor.Z),
                    new PreviewPoint3(anchor.X, anchor.Y + .001f, anchor.Z), new PreviewPoint3(anchor.X, anchor.Y, anchor.Z + .001f) })
                { var projected = Project(offset); scaleSquared += Square((projected.X - projectedAnchor.X) * normalX + (projected.Y - projectedAnchor.Y) * normalY); }
                var label = ManualDrawAnnotationLayout.Length(a.X, a.Y, b.X, b.Y, resources.Width(dimension.Text), .5f * (float)Math.Sqrt(scaleSquared));
                if (label == null) continue;
                var bodyArea = bodies.TryGetValue(dimension.ObjectId, out var ownerArea) ? ownerArea : System.Windows.Rect.Empty;
                var placed = ManualDrawAnnotationLayout.Avoid(label, bodyArea, labels, _viewport[2], _viewport[3],
                    10f + .5f * (float)Math.Sqrt(scaleSquared));
                var shiftX = bodyArea.IsEmpty ? 0f : placed.OriginX - label.OriginX;
                var shiftY = bodyArea.IsEmpty ? 0f : placed.OriginY - label.OriginY;
                var annotationPoints = selected.Points.Select(point => new ScreenPoint {
                    X = point.X + shiftX, Y = point.Y + shiftY, Z = point.Z, Visible = point.Visible }).ToArray();
                if (bodyArea.IsEmpty && (placed.OriginX != label.OriginX || placed.OriginY != label.OriginY))
                    Line((a.X + b.X) * .5f, (a.Y + b.Y) * .5f,
                        placed.OriginX + placed.TangentX * placed.Width * .5f - placed.TangentY * ManualDrawAnnotationLabel.Height * .5f,
                        placed.OriginY + placed.TangentY * placed.Width * .5f + placed.TangentX * ManualDrawAnnotationLabel.Height * .5f);
                label = placed; labels.Add(label);
                if (!bodyArea.IsEmpty)
                {
                    Line(selected.Points[0].X, selected.Points[0].Y, annotationPoints[0].X, annotationPoints[0].Y);
                    var last = selected.Points.Length - 1;
                    Line(selected.Points[last].X, selected.Points[last].Y, annotationPoints[last].X, annotationPoints[last].Y);
                }
                for (var index = 1; index < annotationPoints.Length; index++)
                    ManualDrawAnnotationLayout.Line(annotationPoints[index - 1].X, annotationPoints[index - 1].Y, annotationPoints[index].X, annotationPoints[index].Y, label, Line);
                Arrow(annotationPoints[0], annotationPoints[1], label);
                Arrow(annotationPoints[annotationPoints.Length - 1], annotationPoints[annotationPoints.Length - 2], label);
                DrawDimensionText(resources, dimension.Text, label, focused);
                hits.Add(new Hit { Dimension = dimension, Label = label, SessionId = frame.SessionId, Revision = frame.Revision, ProjectionRevision = _projectionRevision });
            }
            _hits = hits.ToArray();
        }
        private static void DrawDimensionText(ContextResources resources, string text, ManualDrawAnnotationLabel label, bool focused)
        {
            Gl.glPushMatrix(); Gl.glTranslatef(label.OriginX, label.OriginY, 0f);
            Gl.glMultMatrixf(new[] { label.TangentX, label.TangentY, 0f, 0f, -label.TangentY, label.TangentX, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f });
            Gl.glPushMatrix(); Gl.glScalef(20f, 20f, 1f); resources.Text(text); Gl.glPopMatrix();
            if (focused)
            {
                // Reuse the rendered label's axes and extents; focus changes only the overlay.
                const float padding = 3f;
                var right = label.Width + padding;
                var top = ManualDrawAnnotationLabel.Height + padding;
                Gl.glColor4f(.88f, .14f, .11f, 1f); Gl.glLineWidth(2f);
                Gl.glBegin(2); // GL_LINE_LOOP
                Gl.glVertex3f(-padding, -padding, 0f); Gl.glVertex3f(right, -padding, 0f);
                Gl.glVertex3f(right, top, 0f); Gl.glVertex3f(-padding, top, 0f);
                Gl.glEnd(); Gl.glLineWidth(1f);
            }
            Gl.glPopMatrix();
        }
        private static void Line(float x1, float y1, float x2, float y2)
        { Gl.glBegin(1); Gl.glVertex3f(x1, y1, 0); Gl.glVertex3f(x2, y2, 0); Gl.glEnd(); }
        private static float Square(float value) => value * value;
        private static void Arrow(ScreenPoint tip, ScreenPoint toward, ManualDrawAnnotationLabel label)
        {
            var dx = toward.X - tip.X; var dy = toward.Y - tip.Y; var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1f) return;
            dx /= length; dy /= length;
            if (label.Contains(tip.X, tip.Y, 4f) || label.Contains(tip.X + dx * 8f, tip.Y + dy * 8f, 4f)) return;
            Gl.glBegin(4); Gl.glVertex3f(tip.X, tip.Y, 0f);
            Gl.glVertex3f(tip.X + dx * 8f - dy * 2.2f, tip.Y + dy * 8f + dx * 2.2f, 0f);
            Gl.glVertex3f(tip.X + dx * 8f + dy * 2.2f, tip.Y + dy * 8f - dx * 2.2f, 0f); Gl.glEnd();
        }
        private bool HandleMessage(int message, IntPtr buttons, IntPtr parameter)
        {
            if (message == 0x0215 || message == 0x001f || message == 0x0008) { _leftPressed = false; CancelObjectInput(); return false; }
            if (_stop) return false;
            if (message == 0x02a3) { _trackingMouseLeave = false; ResetDimensionHover(); CloseIssuePopup(); return false; }
            var packed = parameter.ToInt64(); var x = (short)(packed & 0xffff); var y = (short)((packed >> 16) & 0xffff);
            if (message == 0x0200)
            {
                TrackIssueMouseLeave();
                QueueDimensionHover(_leftPressed || _objectPressed || (buttons.ToInt64() & 0x73) != 0 ? null : DimensionAt(x, y), x, y);
            }
            else if (message >= 0x0201 && message <= 0x020e || message == 0x0100 || message == 0x0005) ResetDimensionHover();
            if (message != 0x0201 && message != 0x0202 && message != 0x0203) return HandleObjectMessage(message, buttons, parameter);
            if (message == 0x0202 && _leftPressed) { _leftPressed = false; ReleaseCapture(); return true; }
            if (message == 0x0202 && _objectPressed) return HandleObjectMessage(message, buttons, parameter);
            if ((buttons.ToInt64() & 0x0010) != 0) return false;
            var hit = DimensionAt(x, y);
            if (hit != null && (hit.Dimension.IsReadOnly || !CanEditDimension(hit.Dimension))) hit = null;
            if (hit == null) return HandleObjectMessage(message, buttons, parameter);
            if (message == 0x0201 || message == 0x0203) { _leftPressed = true; SetCapture(_messages.Handle); }
            if (message == 0x0203)
            {
                var point = new NativePoint { X = x, Y = y }; ClientToScreen(_messages.Handle, ref point);
                _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!_stop && _frame?.SessionId == hit.SessionId && _frame.Revision == hit.Revision && _projectionRevision == hit.ProjectionRevision)
                        EditRequested?.Invoke(new BridgeDimensionEditRequest(hit.Dimension, point.X, point.Y));
                }));
            }
            return true;
        }
        public void Dispose()
        {
            _dispatcher.VerifyAccess(); _stop = true; _idleTimer.Stop(); _idleTimer.Tick -= IdleFrame; Clear(); _messages.Dispose();
            DisposeDimensionHover();
            _orientationTimer.Stop(); _orientationTimer.Tick -= OrientationSettled;
            BindEditor(null); _redrawTimer.Stop(); _redrawTimer.Tick -= RedrawScheduled;
            // Resources are deleted only when the host next makes their GL context current.
            var current = Gl.wglGetCurrentContext();
            if (_contexts.TryGetValue(current, out var resources)) { resources.Delete(); _contexts.Remove(current); }
            IsDisposed = _contexts.Count == 0;
        }
        // The OS destroys all GL lists with their owning context. Never delete their IDs in a different context.
        internal void ContextDestroyed() { _contexts.Clear(); _messages.Dispose(); DisposeDimensionHover(); IsDisposed = true; }
        private struct ScreenPoint { internal float X, Y, Z; internal bool Visible; }
        private sealed class Hit { internal BridgePreviewDimension Dimension; internal ManualDrawAnnotationLabel Label; internal Guid SessionId; internal long Revision, ProjectionRevision; }
        private sealed class ViewportMessages : System.Windows.Forms.NativeWindow, IDisposable
        {
            private readonly Func<int, IntPtr, IntPtr, bool> _consume;
            internal ViewportMessages(IntPtr window, Func<int, IntPtr, IntPtr, bool> consume) { _consume = consume; AssignHandle(window); }
            protected override void WndProc(ref System.Windows.Forms.Message message)
            {
                try { if (_consume(message.Msg, message.WParam, message.LParam)) { message.Result = IntPtr.Zero; return; } }
                catch (Exception error)
                {
                    CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("参数草稿视口输入失败", error);
                    if (_consume.Target is BridgeOpenGlViewport owner)
                    {
                        owner.CancelObjectInput();
                        if (owner._viewModel != null) owner._viewModel.Message = "视口操作未完成：" + error.Message;
                    }
                }
                base.WndProc(ref message);
            }
            public void Dispose() { ReleaseHandle(); }
        }
        private sealed class ContextResources
        {
            private sealed class MeshLists { internal BridgeMeshShape Shape; internal uint List; internal long Access; }
            private readonly Dictionary<string, MeshLists> _meshes = new Dictionary<string, MeshLists>();
            private long _meshBytes, _access;
            internal uint Mesh(BridgeMeshInstance instance)
            {
                if (_meshes.TryGetValue(instance.GeometryKey, out var cached))
                {
                    if (cached.Shape.Signature == instance.Shape.Signature) { cached.Access = ++_access; return cached.List; }
                    Gl.glDeleteLists(cached.List, 2); _meshBytes -= cached.Shape.Bytes; _meshes.Remove(instance.GeometryKey);
                }
                while (_meshes.Count != 0 && _meshBytes + instance.Shape.Bytes > 192L * 1024 * 1024)
                {
                    var oldest = _meshes.OrderBy(pair => pair.Value.Access).First(); Gl.glDeleteLists(oldest.Value.List, 2);
                    _meshBytes -= oldest.Value.Shape.Bytes; _meshes.Remove(oldest.Key);
                }
                var start = Gl.glGenLists(2); if (start == 0) throw new InvalidOperationException("OpenGL网格缓存分配失败。");
                Gl.glNewList(start, 0x1300);
                DrawFaces(instance.Positions, instance.Quads, 4, instance.Shape.QuadNormals); DrawFaces(instance.Positions, instance.Triangles, 3, instance.Shape.TriangleNormals); Gl.glEndList();
                Gl.glNewList(start + 1, 0x1300); DrawFaces(instance.Shape.EdgePositions, instance.Edges, 2); Gl.glEndList();
                _meshes.Add(instance.GeometryKey, new MeshLists { Shape = instance.Shape, List = start, Access = ++_access }); _meshBytes += instance.Shape.Bytes;
                return start;
            }
            private uint _font;
            private GlyphMetrics[] _metrics;
            private readonly Dictionary<char, Tuple<uint, GlyphMetrics>> _unicodeGlyphs = new Dictionary<char, Tuple<uint, GlyphMetrics>>();
            internal void EnsureFont()
            {
                if (_font != 0) return;
                var lists = Gl.glGenLists(128); if (lists == 0) throw new InvalidOperationException("OpenGL数字字形缓存分配失败。");
                _metrics = new GlyphMetrics[128];
                var font = CreatePreviewFontWithFallback("Consolas");
                if (font == IntPtr.Zero) { Gl.glDeleteLists(lists, 128); throw new InvalidOperationException("系统预览字体资源创建失败。"); }
                var dc = Gl.wglGetCurrentDC(); var old = SelectObject(dc, font);
                try
                { if (!Gl.wglUseFontOutlinesW(dc, 0, 128, lists, 0f, 0f, 1, _metrics)) throw new InvalidOperationException("OpenGL数字字形创建失败。"); _font = lists; }
                catch { Gl.glDeleteLists(lists, 128); throw; }
                finally { SelectObject(dc, old); DeleteObject(font); }
            }
            private Tuple<uint, GlyphMetrics> UnicodeGlyph(char character)
            {
                if (_unicodeGlyphs.TryGetValue(character, out var glyph)) return glyph;
                var list = Gl.glGenLists(1); var metrics = new GlyphMetrics[1];
                var font = CreatePreviewFontWithFallback("Microsoft YaHei");
                if (list == 0 || font == IntPtr.Zero) { if (list != 0) Gl.glDeleteLists(list, 1); if (font != IntPtr.Zero) DeleteObject(font); throw new InvalidOperationException("视口中文字体创建失败。"); }
                var dc = Gl.wglGetCurrentDC(); var old = SelectObject(dc, font);
                try { if (!Gl.wglUseFontOutlinesW(dc, character, 1, list, 0f, 0f, 1, metrics)) throw new InvalidOperationException("视口中文标签字形创建失败。"); }
                catch { Gl.glDeleteLists(list, 1); throw; }
                finally { SelectObject(dc, old); DeleteObject(font); }
                glyph = Tuple.Create(list, metrics[0]); _unicodeGlyphs.Add(character, glyph); return glyph;
            }
            /// <summary>数字与中文共用指定字体、黑体、当前系统字体的选择顺序。</summary>
            private static IntPtr CreatePreviewFontWithFallback(string preferredFamilyName)
            {
                foreach (string candidateFamilyName in new[] { preferredFamilyName, "SimHei" })
                {
                    string resolvedFamilyName;
                    try
                    {
                        using (var candidateFamily = new System.Drawing.FontFamily(candidateFamilyName))
                            resolvedFamilyName = candidateFamily.Name;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    IntPtr candidateFont = CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, resolvedFamilyName);
                    if (candidateFont != IntPtr.Zero) return candidateFont;
                }
                return CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0,
                    System.Drawing.SystemFonts.MessageBoxFont.Name);
            }

            internal float Width(string text) => text.Sum(character => (character < 128 ? _metrics[character] : UnicodeGlyph(character).Item2).CellIncX * 20f - .8f);
            internal void Text(string text) { foreach (var character in text) { Gl.glCallList(character < 128 ? _font + character : UnicodeGlyph(character).Item1); Gl.glTranslatef(-.04f, 0f, 0f); } }
            internal void Delete() { if (_font != 0) Gl.glDeleteLists(_font, 128); _font = 0; foreach (var glyph in _unicodeGlyphs.Values) Gl.glDeleteLists(glyph.Item1, 1); _unicodeGlyphs.Clear(); foreach (var mesh in _meshes.Values) Gl.glDeleteLists(mesh.List, 2); _meshes.Clear(); _meshBytes = 0; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct GlyphMetrics { internal float BlackBoxX, BlackBoxY, OriginX, OriginY, CellIncX, CellIncY; }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height,int width,int escape,int orientation,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint pitch,string face);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        private static class Gl
        {
            [DllImport("opengl32.dll")] internal static extern void glPushAttrib(uint mask);
            [DllImport("opengl32.dll")] internal static extern void glPopAttrib();
            [DllImport("opengl32.dll")] internal static extern void glMatrixMode(uint mode);
            [DllImport("opengl32.dll")] internal static extern void glPushMatrix();
            [DllImport("opengl32.dll")] internal static extern void glPopMatrix();
            [DllImport("opengl32.dll")] internal static extern void glLoadIdentity();
            [DllImport("opengl32.dll")] internal static extern void glLoadMatrixf(float[] matrix);
            [DllImport("opengl32.dll")] internal static extern void glGetFloatv(uint name, float[] values);
            [DllImport("opengl32.dll")] internal static extern void glGetIntegerv(uint name, int[] values);
            [DllImport("opengl32.dll")] internal static extern void glMultMatrixf(float[] matrix);
            [DllImport("opengl32.dll")] internal static extern void glScalef(float x, float y, float z);
            [DllImport("opengl32.dll")] internal static extern void glTranslatef(float x, float y, float z);
            [DllImport("opengl32.dll")] internal static extern void glEnable(uint mode);
            [DllImport("opengl32.dll")] internal static extern void glDisable(uint mode);
            [DllImport("opengl32.dll")] internal static extern void glDepthMask(byte flag);
            [DllImport("opengl32.dll")] internal static extern void glDepthFunc(uint function);
            [DllImport("opengl32.dll")] internal static extern void glColorMask(byte red, byte green, byte blue, byte alpha);
            [DllImport("opengl32.dll")] internal static extern void glPolygonOffset(float factor, float units);
            [DllImport("opengl32.dll")] internal static extern void glPolygonMode(uint face, uint mode);
            [DllImport("opengl32.dll")] internal static extern void glLineStipple(int factor, ushort pattern);
            [DllImport("opengl32.dll")] internal static extern void glBlendFunc(uint source, uint destination);
            [DllImport("opengl32.dll")] internal static extern void glColor4f(float r, float g, float b, float a);
            [DllImport("opengl32.dll")] internal static extern void glLineWidth(float width);
            [DllImport("opengl32.dll")] internal static extern void glBegin(uint mode);
            [DllImport("opengl32.dll")] internal static extern void glEnd();
            [DllImport("opengl32.dll")] internal static extern void glVertex3f(float x, float y, float z);
            [DllImport("opengl32.dll")] internal static extern void glNormal3f(float x, float y, float z);
            [DllImport("opengl32.dll")] internal static extern void glColorMaterial(uint face,uint mode);
            [DllImport("opengl32.dll")] internal static extern void glLightfv(uint light,uint name,float[] value);
            [DllImport("opengl32.dll")] internal static extern void glNewList(uint list,uint mode);
            [DllImport("opengl32.dll")] internal static extern void glEndList();
            [DllImport("opengl32.dll")] internal static extern void glOrtho(double left,double right,double bottom,double top,double near,double far);
            [DllImport("opengl32.dll")] internal static extern uint glGenLists(int range);
            [DllImport("opengl32.dll")] internal static extern void glDeleteLists(uint start,int range);
            [DllImport("opengl32.dll")] internal static extern void glCallList(uint list);
            [DllImport("opengl32.dll")] internal static extern IntPtr wglGetCurrentContext();
            [DllImport("opengl32.dll")] internal static extern IntPtr wglGetCurrentDC();
            [DllImport("opengl32.dll", CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool wglUseFontOutlinesW(IntPtr dc,uint first,uint count,uint list,float deviation,float extrusion,int format,[Out] GlyphMetrics[] metrics);
        }
    }
}
