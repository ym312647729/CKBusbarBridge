using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal sealed partial class BridgeOpenGlViewport
    {
        private sealed class Marker
        {
            internal string ObjectId, Text;
            internal ViewportVector Point;
            internal BridgeDesignIssue Issue;
            internal bool Handle;
        }
        private sealed class MarkerHit { internal Rect Bounds; internal Marker Marker; }
        private Marker[] _markers = Array.Empty<Marker>();
        private MarkerHit[] _markerHits = Array.Empty<MarkerHit>();
        private Popup _issuePopup;
        private BridgeDesignIssue _hoveredIssue;
        private void RefreshMarkers()
        {
            CloseIssuePopup(); _markerHits = Array.Empty<MarkerHit>();
            if (_viewModel?.Editor.CurrentSample == null) { _markers = Array.Empty<Marker>(); return; }
            var markers = new List<Marker>();
            foreach (var node in _viewModel.Objects)
            {
                if (!_viewModel.IsPreviewObjectVisible(node)) continue;
                if (node.Kind == DesignObjectKind.Frame)
                    markers.Add(new Marker { ObjectId = node.Id, Point = ObjectWorldPosition(node), Text = node.PositionLabel });
                else if (_viewModel.ShowHandles && _viewModel.Editor.CanDragObject(node.Id, _viewModel.SameSource) && node.Kind != DesignObjectKind.Insulator)
                    markers.Add(new Marker { ObjectId = node.Id, Point = ObjectWorldPosition(node), Text = "", Handle = true });
            }
            var index = 0;
            foreach (var issue in _viewModel.Issues)
            {
                ViewportVector point;
                var corners = issue.Location?.CopperCorners.Concat(issue.Location.MetalCorners).ToArray();
                if (corners?.Length > 0)
                    point = Vector(DraftCoordinates.ToAssembly(new[] { corners.Average(p => p[0]), corners.Average(p => p[1]), corners.Average(p => p[2]) }));
                else
                {
                    var node = _viewModel.Editor.Find(issue.Location?.RelatedObjectId ?? issue.TargetId);
                    if (node == null)
                    {
                        var route = _viewModel.Editor.CurrentSample.Routes.FirstOrDefault(candidate => candidate.Id == issue.TargetId);
                        var routePoint = route?.PointIds.Select(_viewModel.Editor.Find).FirstOrDefault(candidate => candidate != null);
                        if (routePoint == null) continue;
                        node = routePoint;
                    }
                    point = ObjectWorldPosition(node);
                }
                markers.Add(new Marker { ObjectId = issue.Location?.RelatedObjectId ?? issue.TargetId, Point = point, Issue = issue, Text = "!" + (++index) });
            }
            _markers = markers.ToArray();
        }
        private void DrawMarkers(ContextResources resources)
        {
            var hits = new List<MarkerHit>(); var cells = new Dictionary<long, int>();
            foreach (var marker in _markers)
            {
                var point = marker.Point;
                if (_dragActive && marker.ObjectId != null && _dragObjectAxes.TryGetValue(marker.ObjectId, out var axis)) point += axis * _dragDelta;
                var screen = Project(new PreviewPoint3(point.X / 1000f, point.Y / 1000f, point.Z / 1000f));
                if (!screen.Visible || screen.X < 0 || screen.X > _viewport[2] || screen.Y < 0 || screen.Y > _viewport[3]) continue;
                if (marker.Handle)
                {
                    Gl.glColor4f(1f, .45f, .04f, 1f); Gl.glLineWidth(2f);
                    Line(screen.X - 5, screen.Y, screen.X, screen.Y + 5); Line(screen.X, screen.Y + 5, screen.X + 5, screen.Y);
                    Line(screen.X + 5, screen.Y, screen.X, screen.Y - 5); Line(screen.X, screen.Y - 5, screen.X - 5, screen.Y);
                    hits.Add(new MarkerHit { Bounds = new Rect(screen.X - 7, screen.Y - 7, 14, 14), Marker = marker }); continue;
                }
                var text = marker.Text ?? ""; var width = resources.Width(text) * .7f + 12f;
                var key = ((long)(int)(screen.X / 80f) << 32) | (uint)(int)(screen.Y / 24f);
                cells.TryGetValue(key, out var row); cells[key] = row + 1;
                var x = screen.X + 8f; var y = screen.Y + 10f + row * 24f;
                Gl.glColor4f(1f, 1f, 1f, .85f); Gl.glBegin(7);
                Gl.glVertex3f(x, y, 0); Gl.glVertex3f(x + width, y, 0); Gl.glVertex3f(x + width, y + 22, 0); Gl.glVertex3f(x, y + 22, 0); Gl.glEnd();
                if (marker.Issue != null) Gl.glColor4f(.8f, .05f, .12f, 1f); else Gl.glColor4f(.08f, .16f, .32f, 1f);
                Line(screen.X, screen.Y, x, y);
                Gl.glPushMatrix(); Gl.glTranslatef(x + 6f, y + 5f, 0f); Gl.glScalef(14f, 14f, 1f); resources.Text(text); Gl.glPopMatrix();
                hits.Add(new MarkerHit { Bounds = new Rect(x, y, width, 22), Marker = marker });
            }
            _markerHits = hits.ToArray();
        }
        private Marker MarkerAt(float x, float y) => IsStale || IsPreviewOnly ? null : _markerHits.LastOrDefault(hit => hit.Bounds.Contains(x - _viewport[0], _viewport[3] - (y - _viewport[1])))?.Marker;
        private bool SelectMarker(float x, float y, bool control)
        {
            var marker = MarkerAt(x, y); if (marker == null || _viewModel == null) return false;
            if (marker.Issue == null && !BridgeViewportAppearance.CanSelect(
                _viewModel.Editor.Find(marker.ObjectId)?.Kind == DesignObjectKind.Frame ? "frame" : "copper", _viewModel.SelectedTab)) return false;
            CloseIssuePopup();
            if (marker.Issue != null) { _viewModel.Locate(marker.Issue); _objectPressed = true; SetFocus(_messages.Handle); SetCapture(_messages.Handle); return true; }
            if (control && _viewModel.Editor.Find(marker.ObjectId)?.Kind == DesignObjectKind.Frame) _viewModel.ToggleFrameSelection(marker.ObjectId);
            else _viewModel.Select(marker.ObjectId);
            _objectPressed = true; if (!control && marker.Handle) PrepareDrag(marker.ObjectId, x, y);
            SetFocus(_messages.Handle); SetCapture(_messages.Handle); return true;
        }
        private void UpdateIssuePopup(float x, float y)
        {
            var issue = _objectPressed || DimensionAt(x, y) != null ? null : MarkerAt(x, y)?.Issue;
            if (issue == null) { CloseIssuePopup(); return; }
            if (_issuePopup == null) _issuePopup = new Popup { AllowsTransparency = true, Focusable = false, IsHitTestVisible = false, StaysOpen = true,
                Placement = PlacementMode.AbsolutePoint, Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Crimson, BorderThickness = new Thickness(1), Padding = new Thickness(8),
                    Child = new TextBlock { MaxWidth = 320, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black } } };
            if (!ReferenceEquals(issue, _hoveredIssue)) ((TextBlock)((Border)_issuePopup.Child).Child).Text = issue.Source + "：" + issue.Message;
            _hoveredIssue = issue;
            var point = new NativePoint { X = (int)x, Y = (int)y }; ClientToScreen(_messages.Handle, ref point);
            var scale = 96f / Math.Max(96, GetDpiForWindow(_messages.Handle));
            _issuePopup.HorizontalOffset = point.X * scale + 16f; _issuePopup.VerticalOffset = point.Y * scale + 18f; _issuePopup.IsOpen = true;
        }
        private void CloseIssuePopup() { if (_issuePopup != null) _issuePopup.IsOpen = false; _hoveredIssue = null; }
        private bool _trackingMouseLeave;
        private void TrackIssueMouseLeave()
        {
            if (_trackingMouseLeave) return;
            var tracking = new MouseLeaveTracking { Size = (uint)Marshal.SizeOf(typeof(MouseLeaveTracking)), Flags = 2, Window = _messages.Handle };
            _trackingMouseLeave = TrackMouseEvent(ref tracking);
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseLeaveTracking { internal uint Size, Flags; internal IntPtr Window; internal uint HoverTime; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TrackMouseEvent(ref MouseLeaveTracking tracking);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    }
}
