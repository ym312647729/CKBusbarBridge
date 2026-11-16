using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace CKBusbarBridge.DesignPreview
{
    internal static class BridgeViewportArea
    {
        // Both rectangles are physical screen pixels, independent of WPF's per-monitor DIP scale.
        internal static Rect Visible(Rect viewport, params Rect[] panels)
        {
            var blocked = panels.Select(panel => Rect.Intersect(viewport, panel))
                .Where(area => !area.IsEmpty && area.Width > 0 && area.Height > 0).Distinct().ToArray();
            var edges = blocked.SelectMany(area => new[] { area.Left, area.Right })
                .Concat(new[] { viewport.Left, viewport.Right }).Distinct().OrderBy(edge => edge).ToArray();
            var best = new Rect(viewport.TopLeft, new Size(0, 0));
            for (var left = 0; left < edges.Length - 1; left++) for (var right = left + 1; right < edges.Length; right++)
            {
                var top = viewport.Top;
                foreach (var obstacle in blocked.Where(area => area.Left < edges[right] && area.Right > edges[left]).OrderBy(area => area.Top))
                {
                    if (obstacle.Top > top) Keep(new Rect(edges[left], top, edges[right] - edges[left], obstacle.Top - top), ref best);
                    top = Math.Max(top, obstacle.Bottom);
                }
                if (top < viewport.Bottom) Keep(new Rect(edges[left], top, edges[right] - edges[left], viewport.Bottom - top), ref best);
            }
            return best;
        }
        private static void Keep(Rect candidate, ref Rect best)
        { if (candidate.Width * candidate.Height > best.Width * best.Height) best = candidate; }
        // The bridge window may sit on another monitor, so it never limits framing.
        // Only SW's own docked panes that overlay the graphics HWND are excluded.
        internal static Rect ClientArea(IntPtr viewport)
        {
            if (!GetClientRect(viewport, out var client)) throw new InvalidOperationException("无法取得预览视口范围。");
            var origin = new NativePoint();
            if (!ClientToScreen(viewport, ref origin)) throw new InvalidOperationException("无法取得预览视口位置。");
            var full = new Rect(origin.X, origin.Y, client.Right, client.Bottom);
            var obstructions = new List<Rect>();
            // Docked SW panes can overlap the graphics HWND. Use real visible child windows, not fixed tree widths or captions.
            EnumChildWindows(GetAncestor(viewport, 2), (child, state) =>
            {
                if (child == viewport || IsChild(child, viewport) || !IsWindowVisible(child) || !GetWindowRect(child, out var bounds)) return true;
                var rectangle = new Rect(bounds.Left, bounds.Top, Math.Max(0, bounds.Right - bounds.Left), Math.Max(0, bounds.Bottom - bounds.Top));
                var overlap = Rect.Intersect(full, rectangle);
                if (overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0 || rectangle.Contains(full)) return true;
                foreach (var x in new[] { overlap.Left + 1, (overlap.Left + overlap.Right) / 2, overlap.Right - 1 })
                    foreach (var y in new[] { overlap.Top + 1, (overlap.Top + overlap.Bottom) / 2, overlap.Bottom - 1 })
                    {
                        var hit = WindowFromPoint(new NativePoint { X = (int)x, Y = (int)y });
                        if (hit != child && !IsChild(child, hit)) continue;
                        obstructions.Add(overlap); return true;
                    }
                return true;
            }, IntPtr.Zero);
            var available = Visible(full, obstructions.Where(area => !obstructions.Any(other => other != area && other.Contains(area))).ToArray());
            if (available.Width < 32 || available.Height < 32) throw new InvalidOperationException("SolidWorks 停靠窗格遮住了视口，请调整窗格后重新取景。");
            available.Offset(-origin.X, -origin.Y);
            return available;
        }
        internal static float FitScale(Rect projected, Rect available)
        {
            if (projected.IsEmpty || projected.Width <= 0 || projected.Height <= 0) throw new InvalidOperationException("预览投影范围无效。");
            return .90f * Math.Min((float)(available.Width / projected.Width), (float)(available.Height / projected.Height));
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        private delegate bool EnumerateWindow(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumerateWindow callback, IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    }
}
