using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    internal sealed class FaceToWallInternalSupportLayout
    {
        internal double X { get; set; }
        internal double Height { get; set; }
        internal double[] PhaseCenters { get; set; }
    }

    internal sealed class FaceToWallInternalSupportPlanner
    {
        private sealed class Region
        {
            internal string Phase;
            internal double Start, End, Across, MinimumAcross, MaximumAcross, Top;
        }

        private Region[] Regions(BridgeDesignSample sample)
        {
            var raw = sample.Routes.SelectMany(route => route.PhysicalSolids
            .Where(s => s.Length == 8 && Enumerable.Range(0, 3).All(axis =>
                s.Select(p => Math.Round(p[axis], 4)).Distinct().Count() == 2))
            .Select(s => new Region { Phase = route.Phase, Start = s.Min(p => p[0]), End = s.Max(p => p[0]),
                Across = (s.Min(p => p[1]) + s.Max(p => p[1])) / 2,
                MinimumAcross = s.Min(p => p[1]), MaximumAcross = s.Max(p => p[1]), Top = s.Min(p => p[2]) }))
                .ToArray();
            var merged = new List<Region>();
            foreach (var region in raw.OrderBy(r => r.Start).ThenBy(r => r.Phase, StringComparer.Ordinal).ThenBy(r => r.Top))
            {
                var previous = merged.FirstOrDefault(r => r.Phase == region.Phase && Math.Abs(r.Top - region.Top) < .01 &&
                    Math.Abs(r.MinimumAcross - region.MinimumAcross) < .01 && Math.Abs(r.MaximumAcross - region.MaximumAcross) < .01 &&
                    r.End >= region.Start - .01);
                if (previous == null) merged.Add(region);
                else previous.End = Math.Max(previous.End, region.End);
            }
            return merged.Where(r => r.End - r.Start >= BusbarBridgeDraftPreviewSceneFactory.InsulatorWidthMm).ToArray();
        }

        internal FaceToWallInternalSupportLayout Create(BridgeDesignSample sample, BridgeDesignObject frame)
        {
            var regions = Regions(sample);
            var half = BusbarBridgeDraftPreviewSceneFactory.InsulatorWidthMm / 2;
            var floor = frame.Z - frame.Height / 2 + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
            var margin = BusbarBridgeInsulatorClearance.JointCenterClearanceMm;
            var headObjects = sample.Objects.Any(o => o.Id == frame.Id) ? sample.Objects : sample.Objects.Concat(new[] { frame }).ToArray();
            var heads = new BusbarBridgeInsulatorClearance().HeadEnvelopes(sample, headObjects)
                .Where(h => h.Length > 0).Select(h => new[] { h.Min(p => p[0]), h.Max(p => p[0]), h.Min(p => p[1]), h.Max(p => p[1]) }).ToArray();
            var candidates = regions.SelectMany(r => new[] { r.Start + half, r.End - half })
                .Concat(heads.SelectMany(h => new[] { h[0] - margin, h[1] + margin }))
                .Concat(new[] { frame.X }).Distinct().Where(x => x - half >= frame.X - frame.Length / 2 + 24 &&
                    x + half <= frame.X + frame.Length / 2 - 24);
            var layouts = new List<FaceToWallInternalSupportLayout>();
            foreach (var x in candidates)
            {
                var phases = new[] { "A", "B", "C" }.Select(phase => regions.Where(r => r.Phase == phase &&
                    r.Start <= x - half + .01 && r.End >= x + half - .01 && r.Top > floor + 25 &&
                    r.Top < frame.Z + frame.Height / 2 - 40 && Math.Abs(r.Across - frame.Y) + half < frame.Width / 2 - 20)
                    .OrderBy(r => r.Top).ThenBy(r => r.Across).FirstOrDefault()).ToArray();
                if (phases.Any(r => r == null) || phases.Max(r => r.Top) - phases.Min(r => r.Top) > .1) continue;
                var centers = phases.Select(r => r.Across).OrderBy(y => y).ToArray();
                if (Math.Abs(centers[1] - (centers[0] + centers[2]) / 2) > .01 || centers[2] - centers[0] < .01) continue;
                // 模板只支持居中的一体三相支架，特殊排横移后的直段不能生成偏心支架。
                if (Math.Abs(centers[1] - frame.Y) > .01) continue;
                if (centers.Any(y => heads.Any(h => x > h[0] - margin + .001 && x < h[1] + margin - .001 &&
                    y + half > h[2] + .001 && y - half < h[3] - .001))) continue;
                layouts.Add(new FaceToWallInternalSupportLayout { X = x, Height = phases[0].Top - floor, PhaseCenters = centers });
            }
            return layouts.OrderBy(p => p.Height).ThenBy(p => Math.Abs(p.X - frame.X)).ThenBy(p => p.X).FirstOrDefault();
        }

        internal IEnumerable<double> CandidateDeltas(BridgeDesignSample sample, BridgeDesignObject frame, BridgeDesignObject child)
        {
            var current = frame.X + child.X;
            var half = child.Length / 2;
            return Regions(sample).SelectMany(r => new[] { r.Start + half - current, r.End - half - current });
        }

        internal bool Supports(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            IEnumerable<BridgeDesignObject> members, double delta)
        {
            var regions = Regions(sample);
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            return members.All(child =>
            {
                var p = geometry.WorldPosition(child, objects);
                var top = p[2] + child.Height / 2;
                return regions.Any(r => r.Start <= p[0] + delta - child.Length / 2 + .01 &&
                    r.End >= p[0] + delta + child.Length / 2 - .01 && r.MinimumAcross <= p[1] + .01 &&
                    r.MaximumAcross >= p[1] - .01 && Math.Abs(r.Top - top) <= .1);
            });
        }
    }
}
