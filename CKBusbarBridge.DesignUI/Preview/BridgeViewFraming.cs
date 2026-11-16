using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace CKBusbarBridge.DesignPreview
{
    internal static class BridgeViewFraming
    {
        // SW stores three basis vectors in elements 0..8. Geometry stays in mm here.
        internal static float[] OrientationMatrix(IReadOnlyList<float> values)
        {
            if (values == null || values.Count < 9) throw new InvalidOperationException("视图缺少完整朝向矩阵。");
            return new[] { values[0], values[1], values[2], 0f, values[3], values[4], values[5], 0f,
                values[6], values[7], values[8], 0f, 0f, 0f, 0f, 1f };
        }
        internal static ViewportVector[] ZoomRectangle(ViewportBounds bounds, float[] orientation)
        {
            var viewBounds = ViewportBounds.Empty;
            foreach (var x in new[] { bounds.Minimum.X, bounds.Maximum.X })
                foreach (var y in new[] { bounds.Minimum.Y, bounds.Maximum.Y })
                    foreach (var z in new[] { bounds.Minimum.Z, bounds.Maximum.Z })
                        viewBounds.Include(ViewportMatrix.Transform(orientation, new ViewportVector(x, y, z)));
            var marginX = Math.Max(1f, (viewBounds.Maximum.X - viewBounds.Minimum.X) * .05f);
            var marginY = Math.Max(1f, (viewBounds.Maximum.Y - viewBounds.Minimum.Y) * .05f);
            var depth = (viewBounds.Minimum.Z + viewBounds.Maximum.Z) * .5f;
            var inverse = ViewportMatrix.Inverse(orientation);
            return new[] { ViewportMatrix.Transform(inverse, new ViewportVector(viewBounds.Minimum.X - marginX, viewBounds.Minimum.Y - marginY, depth)),
                ViewportMatrix.Transform(inverse, new ViewportVector(viewBounds.Maximum.X + marginX, viewBounds.Maximum.Y + marginY, depth)) };
        }
        internal static bool IsStandardDirection(float[] orientation)
        {
            var direction = new ViewportVector(Math.Abs(orientation[2]), Math.Abs(orientation[6]), Math.Abs(orientation[10])).Unit;
            var axisAlignment = Math.Max(direction.X, Math.Max(direction.Y, direction.Z));
            var diagonalAlignment = (direction.X + direction.Y + direction.Z) / (float)Math.Sqrt(3f);
            return Angle(axisAlignment) <= 1.0f || Angle(diagonalAlignment) <= 1.0f;
        }
        private static float Angle(float cosine) => (float)Math.Acos(Math.Max(-1f, Math.Min(1f, cosine))) * 180f / (float)Math.PI;
    }
    internal sealed partial class BridgeOpenGlViewport
    {
        private readonly DispatcherTimer _orientationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        internal event Action ViewOrientationSettled;
        internal void NotifyViewOrientationChanged()
        { if (_stop) return; _orientationTimer.Stop(); _orientationTimer.Start(); }
        private void OrientationSettled(object sender, EventArgs args)
        { _orientationTimer.Stop(); if (!_stop) ViewOrientationSettled?.Invoke(); }
    }
}
