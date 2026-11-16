using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CKBusbarBridge.DesignPreview
{
    // Adapted from copper master's ManualDrawTextLayout / ArrowPreviewFrame length annotations.
    // Screen coordinates use OpenGL's lower-left origin; geometry remains in the original business frame.
    internal sealed class ManualDrawAnnotationLabel
    {
        internal const float Height = 20f;
        internal float OriginX, OriginY, TangentX, TangentY, Width;
        internal Rect Bounds
        {
            get
            {
                var rectangle = Rect.Empty;
                foreach (var x in new[] { 0f, Width }) foreach (var y in new[] { 0f, Height })
                    rectangle.Union(new Point(OriginX + TangentX * x - TangentY * y, OriginY + TangentY * x + TangentX * y));
                return rectangle;
            }
        }
        internal ManualDrawAnnotationLabel Shift(float x, float y) => new ManualDrawAnnotationLabel {
            OriginX = OriginX + x, OriginY = OriginY + y, TangentX = TangentX, TangentY = TangentY, Width = Width };
        internal bool Contains(float screenX, float screenY, float padding = 0f)
        {
            var dx = screenX - OriginX; var dy = screenY - OriginY;
            var localX = dx * TangentX + dy * TangentY;
            var localY = -dx * TangentY + dy * TangentX;
            return localX >= -padding && localX <= Width + padding && localY >= -padding && localY <= Height + padding;
        }
        internal void Local(float screenX, float screenY, out float localX, out float localY)
        {
            var dx = screenX - OriginX; var dy = screenY - OriginY;
            localX = dx * TangentX + dy * TangentY; localY = -dx * TangentY + dy * TangentX;
        }
    }

    internal static class ManualDrawAnnotationLayout
    {
        internal static ManualDrawAnnotationLabel Avoid(ManualDrawAnnotationLabel label, Rect body,
            IReadOnlyList<ManualDrawAnnotationLabel> previous, float viewportWidth, float viewportHeight, float bodyPadding = 10f)
        {
            var original = label.Bounds;
            var occupied = previous.Select(item => { var bounds = item.Bounds; bounds.Inflate(4, 4); return bounds; }).ToArray();
            if (body.IsEmpty && !occupied.Any(item => item.IntersectsWith(original))) return label;
            var obstacle = body.IsEmpty ? original : body;
            var candidates = new List<ManualDrawAnnotationLabel>();
            for (var lane = 0; lane < 48; lane++)
            {
                var slide = ((lane + 1) / 2) * (lane % 2 == 0 ? 1f : -1f) * 28f;
                for (var side = 0; side < (body.IsEmpty ? 4 : 2); side++)
                {
                    var outwardX = body.IsEmpty ? (side == 0 ? -1f : side == 1 ? 1f : 0f) : -label.TangentY * (side == 0 ? 1f : -1f);
                    var outwardY = body.IsEmpty ? (side == 2 ? 1f : side == 3 ? -1f : 0f) : label.TangentX * (side == 0 ? 1f : -1f);
                    var distance = Escape(original, obstacle, outwardX, outwardY, bodyPadding);
                    var dx = outwardX * distance + (body.IsEmpty ? (side >= 2 ? slide : 0f) : label.TangentX * slide);
                    var dy = outwardY * distance + (body.IsEmpty ? (side < 2 ? slide : 0f) : label.TangentY * slide);
                    var candidate = label.Shift(dx, dy);
                    // Push along the outward lane until every already placed text rectangle is clear.
                    for (var pass = 0; pass <= occupied.Length; pass++)
                    {
                        var conflicts = occupied.Where(item => item.IntersectsWith(candidate.Bounds)).ToArray();
                        if (conflicts.Length == 0) break;
                        var box = candidate.Bounds;
                        var movement = conflicts.Max(item => Escape(box, item, outwardX, outwardY, 4f));
                        candidate = candidate.Shift(outwardX * movement, outwardY * movement);
                    }
                    var bounds = candidate.Bounds;
                    if (!occupied.Any(item => item.IntersectsWith(bounds)) && (body.IsEmpty || !body.IntersectsWith(bounds))) candidates.Add(candidate);
                }
                var visible = candidates.Where(item => item.Bounds.Left >= 4 && item.Bounds.Top >= 4 &&
                    item.Bounds.Right <= viewportWidth - 4 && item.Bounds.Bottom <= viewportHeight - 4).ToArray();
                if (visible.Length > 0) return visible.OrderBy(item => {
                    var dx = item.OriginX - label.OriginX; var dy = item.OriginY - label.OriginY;
                    return dx * dx + dy * dy;
                }).First();
            }
            return candidates.OrderBy(item => {
                var bounds = item.Bounds;
                var clipped = bounds.Left < 4 || bounds.Top < 4 || bounds.Right > viewportWidth - 4 || bounds.Bottom > viewportHeight - 4;
                var dx = item.OriginX - label.OriginX; var dy = item.OriginY - label.OriginY;
                return (clipped ? 1e12f : 0f) + dx * dx + dy * dy;
            }).First();
        }
        private static float Escape(Rect moving, Rect obstacle, float directionX, float directionY, float padding)
        {
            var distance = float.PositiveInfinity;
            if (directionX > 0f) distance = Math.Min(distance, (float)(obstacle.Right - moving.Left + padding) / directionX);
            if (directionX < 0f) distance = Math.Min(distance, (float)(moving.Right - obstacle.Left + padding) / -directionX);
            if (directionY > 0f) distance = Math.Min(distance, (float)(obstacle.Bottom - moving.Top + padding) / directionY);
            if (directionY < 0f) distance = Math.Min(distance, (float)(moving.Bottom - obstacle.Top + padding) / -directionY);
            return Math.Max(0f, distance);
        }
        internal static ManualDrawAnnotationLabel Length(float firstX, float firstY, float secondX, float secondY,
            float width, float bottomGapPixels)
        {
            var tangentX = secondX - firstX; var tangentY = secondY - firstY;
            var projectedLength = (float)Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
            if (projectedLength < 1f) return null;
            tangentX /= projectedLength; tangentY /= projectedLength;
            if (tangentX < 0f) { tangentX = -tangentX; tangentY = -tangentY; }
            // Same centered placement and 0.5 mm model-scaled bottom gap as ManualDraw.
            var centerX = (firstX + secondX) * .5f - tangentY * (ManualDrawAnnotationLabel.Height * .5f + bottomGapPixels);
            var centerY = (firstY + secondY) * .5f + tangentX * (ManualDrawAnnotationLabel.Height * .5f + bottomGapPixels);
            return new ManualDrawAnnotationLabel { TangentX = tangentX, TangentY = tangentY, Width = width,
                OriginX = centerX - tangentX * width * .5f + tangentY * ManualDrawAnnotationLabel.Height * .5f,
                OriginY = centerY - tangentY * width * .5f - tangentX * ManualDrawAnnotationLabel.Height * .5f };
        }

        // Clip the line against the entire transparent text rectangle, including spaces.
        internal static void Line(float firstX, float firstY, float secondX, float secondY,
            ManualDrawAnnotationLabel label, Action<float, float, float, float> draw)
        {
            label.Local(firstX, firstY, out var localX, out var localY);
            var dx = secondX - firstX; var dy = secondY - firstY;
            var start = 0f; var end = 1f;
            if (!Clip(localX, dx * label.TangentX + dy * label.TangentY, label.Width, ref start, ref end) ||
                !Clip(localY, -dx * label.TangentY + dy * label.TangentX, ManualDrawAnnotationLabel.Height, ref start, ref end))
                draw(firstX, firstY, secondX, secondY);
            else
            {
                if (start > 0f) draw(firstX, firstY, firstX + dx * start, firstY + dy * start);
                if (end < 1f) draw(firstX + dx * end, firstY + dy * end, secondX, secondY);
            }
        }
        private static bool Clip(float start, float delta, float maximum, ref float hiddenStart, ref float hiddenEnd)
        {
            if (delta == 0f) return start >= 0f && start <= maximum;
            var first = -start / delta; var second = (maximum - start) / delta;
            hiddenStart = Math.Max(hiddenStart, Math.Min(first, second)); hiddenEnd = Math.Min(hiddenEnd, Math.Max(first, second));
            return hiddenStart <= hiddenEnd;
        }
    }
}
