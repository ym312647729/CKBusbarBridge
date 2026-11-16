using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CKBusbarBridge.DesignPreview
{
    internal sealed partial class BridgeOpenGlViewport
    {
        private readonly DispatcherTimer _dimensionHoverTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(500) };
        private Popup _dimensionPopup;
        private Hit _pendingDimensionHover, _activeDimensionHover;
        private int _dimensionHoverX, _dimensionHoverY;
        private bool _dimensionHoverQueued;

        private bool IsCurrentDimensionHit(Hit hit) => hit != null && _frame?.SessionId == hit.SessionId &&
            _frame.Revision == hit.Revision && _projectionRevision == hit.ProjectionRevision;

        private Hit DimensionAt(float x, float y) => IsStale || IsPreviewOnly ? null : _hits.LastOrDefault(hit =>
            IsCurrentDimensionHit(hit) && hit.Label.Contains(x - _viewport[0], _viewport[3] - (y - _viewport[1])));

        // Native input and GL callbacks only record the latest hit. Popup work stays on the original STA dispatcher.
        private void QueueDimensionHover(Hit hit, int x, int y)
        {
            _pendingDimensionHover = hit; _dimensionHoverX = x; _dimensionHoverY = y;
            if (_dimensionHoverQueued || _stop || IsDisposed || _dispatcher.HasShutdownStarted) return;
            _dimensionHoverQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ApplyDimensionHover));
        }

        private void ResetDimensionHover() => QueueDimensionHover(null, 0, 0);

        private void ApplyDimensionHover()
        {
            _dimensionHoverQueued = false;
            var hit = _pendingDimensionHover;
            if (_stop || IsDisposed || IsStale || IsPreviewOnly || !IsCurrentDimensionHit(hit))
            { CloseDimensionHover(); return; }
            if (!IsCurrentDimensionHit(_activeDimensionHover) || !ReferenceEquals(_activeDimensionHover.Dimension, hit.Dimension))
            {
                CloseDimensionHover(); _activeDimensionHover = hit; _dimensionHoverTimer.Start();
            }
            if (_dimensionPopup?.IsOpen == true) PositionDimensionPopup();
        }

        private void DimensionHoverElapsed(object sender, EventArgs args)
        {
            _dimensionHoverTimer.Stop();
            var hit = DimensionAt(_dimensionHoverX, _dimensionHoverY);
            if (_stop || IsDisposed || _objectPressed || _leftPressed || !IsCurrentDimensionHit(_activeDimensionHover) ||
                hit == null || !ReferenceEquals(hit.Dimension, _activeDimensionHover.Dimension))
            { CloseDimensionHover(); return; }
            if (_dimensionPopup == null)
                _dimensionPopup = new Popup { AllowsTransparency = true, Focusable = false, IsHitTestVisible = false,
                    StaysOpen = true, Placement = PlacementMode.AbsolutePoint,
                    Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 5, 8, 5), Child = new TextBlock { MaxWidth = 360,
                            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black } } };
            ((TextBlock)((Border)_dimensionPopup.Child).Child).Text = hit.Dimension.Label;
            CloseIssuePopup(); PositionDimensionPopup(); _dimensionPopup.IsOpen = true;
        }

        private void PositionDimensionPopup()
        {
            var point = new NativePoint { X = _dimensionHoverX, Y = _dimensionHoverY };
            ClientToScreen(_messages.Handle, ref point);
            var scale = 96f / Math.Max(96, GetDpiForWindow(_messages.Handle));
            _dimensionPopup.HorizontalOffset = point.X * scale + 16f;
            _dimensionPopup.VerticalOffset = point.Y * scale + 18f;
        }

        private void CloseDimensionHover()
        {
            _dimensionHoverTimer.Stop(); _activeDimensionHover = null;
            if (_dimensionPopup != null) _dimensionPopup.IsOpen = false;
        }

        private void DisposeDimensionHover()
        {
            CloseDimensionHover(); _pendingDimensionHover = null;
            _dimensionHoverTimer.Tick -= DimensionHoverElapsed;
        }
    }
}
