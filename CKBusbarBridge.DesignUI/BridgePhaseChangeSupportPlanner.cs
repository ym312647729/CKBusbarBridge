using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgePhaseChangeSupportPlanner
    {
        // 024模板吊装坐标系原点在顶面下4mm；正式装配还要核验实际坐标系，禁止Box回退。
        internal const double CeilingInsetMm = 4d;
        internal const string IssueSource = "换相吊装";
        private const double Tolerance = BridgeDesignPrecision.LengthComparisonToleranceMm;
        private sealed class Member
        {
            internal BridgeDesignRoute Route;
            internal double[][] Points;
            internal string Key;
            internal double Baseline, Radius;
            internal SupportFace[] Faces;
        }
        private sealed class SupportFace
        {
            internal double Start, End, Y, Top;
        }
        private readonly List<Member> _members = new List<Member>();
        private readonly HashSet<string> _sections = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<double[]> _sectionRanges = new List<double[]>();
        private double _start = double.PositiveInfinity, _end = double.NegativeInfinity;

        internal BridgePhaseChangeSupportPlanner(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample?.ScenarioKey != "FaceToFace") return;
            var nodes = objects.ToDictionary(o => o.Id);
            var a = sample.CopperEditRoutes.SingleOrDefault(r => r.Phase == "A");
            var c = sample.CopperEditRoutes.SingleOrDefault(r => r.Phase == "C");
            if (a == null || c == null || a.PointIds.Count != c.PointIds.Count) return;
            for (var i = 1; i < a.PointIds.Count; i++)
            {
                if (!nodes.ContainsKey(a.PointIds[i - 1]) || !nodes.ContainsKey(a.PointIds[i]) ||
                    !nodes.ContainsKey(c.PointIds[i - 1]) || !nodes.ContainsKey(c.PointIds[i])) continue;
                var previous = nodes[a.PointIds[i - 1]].Y - nodes[c.PointIds[i - 1]].Y;
                var next = nodes[a.PointIds[i]].Y - nodes[c.PointIds[i]].Y;
                if (Math.Abs(previous) > Tolerance && Math.Abs(next) > Tolerance && Math.Sign(previous) != Math.Sign(next))
                {
                    _sections.Add(BridgeCopperSection.IdFor(i));
                    _sectionRanges.Add(new[] { Math.Min(nodes[a.PointIds[i - 1]].X, nodes[a.PointIds[i]].X),
                        Math.Max(nodes[a.PointIds[i - 1]].X, nodes[a.PointIds[i]].X) });
                }
            }
            foreach (var route in sample.Routes.Where(r => r.IsPhysicalGeometry && _sections.Contains(r.CopperSectionId)))
            {
                var native = route.CopyNativePreview();
                if (native == null) continue;
                _members.Add(new Member { Route = route, Key = route.Phase + "/" + native.GroupKey,
                    Points = native.PointList.Select(p => sample.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z })).ToArray(),
                    Radius = Math.Max(native.BarBendingRadius, native.VerticalBendingRadius) });
            }
            foreach (var group in _members.GroupBy(m => m.Key))
            {
                var baseline = group.SelectMany(m => m.Points).Min(p => p[2]);
                foreach (var member in group)
                {
                    member.Baseline = baseline;
                    member.Faces = HorizontalSupportFaces(member).ToArray();
                    foreach (var p in member.Points.Where(p => p[2] > baseline + member.Route.SectionThickness + Tolerance))
                    { _start = Math.Min(_start, p[0]); _end = Math.Max(_end, p[0]); }
                }
            }
        }

        internal bool IsAction(string section, double[] a, double[] b) => section != null && _sections.Contains(section) &&
            Math.Max(a[0], b[0]) >= _start - Tolerance && Math.Min(a[0], b[0]) <= _end + Tolerance;

        internal bool IsPhaseChangeRegion(IEnumerable<double[]> corners)
        {
            if (_sectionRanges.Count == 0) return false;
            var points = corners.ToArray();
            if (points.Length == 0) return false;
            var minimum = points.Min(p => p[0]); var maximum = points.Max(p => p[0]);
            return _sectionRanges.Any(range => maximum >= range[0] - Tolerance && minimum <= range[1] + Tolerance);
        }

        internal bool RequiresHangingAt(BridgeDesignObject frame, double localX)
        {
            if (frame.FrameTemplateKey != "Regular" || _members.Count == 0) return false;
            var x = frame.X + localX * Math.Cos(frame.ModelDirection * Math.PI / 180d);
            return x >= _start - Tolerance && x <= _end + Tolerance;
        }

        internal IEnumerable<double> CandidateDeltas(BridgeDesignObject frame, double localX)
        {
            if (frame.FrameTemplateKey != "Regular" || _start > _end || !IsBridgeAligned(frame)) yield break;
            var angle = frame.ModelDirection * Math.PI / 180d;
            var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
            var halfLength = frame.DriveValue(DesignField.Length) / 2d;
            var localStart = (_start - frame.X) / cosine;
            var localEnd = (_end - frame.X) / cosine;
            var start = Math.Max(Math.Min(localStart, localEnd), -halfLength);
            var end = Math.Min(Math.Max(localStart, localEnd), halfLength);
            if (start > end) yield break;
            var boundaries = _members.SelectMany(member => member.Faces)
                .SelectMany(face => new[] {
                    (face.Start - frame.X) * cosine + (face.Y - frame.Y) * sine,
                    (face.End - frame.X) * cosine + (face.Y - frame.Y) * sine })
                .Where(x => x >= start && x <= end).Concat(new[] { start, end })
                .Distinct().OrderBy(x => x).ToArray();
            for (var index = 0; index < boundaries.Length; index++)
            {
                yield return boundaries[index] - localX;
                // 边界处可能同时命中两块面，开区间内再取一个候选并重新核验。
                if (index > 0)
                    yield return (boundaries[index - 1] + boundaries[index]) / 2d - localX;
            }
        }

        private bool IsBridgeAligned(BridgeDesignObject frame) =>
            Math.Abs(Math.IEEERemainder(frame.ModelDirection, 180d)) <= Tolerance;

        internal BridgeInsulatorMountPlan At(BridgeDesignObject frame, double localX, out string problem)
        {
            problem = null;
            if (!RequiresHangingAt(frame, localX)) return null;
            if (!IsBridgeAligned(frame))
            { problem = "换相吊装尚不支持偏离桥向的基架姿态。"; return null; }
            var angle = frame.ModelDirection * Math.PI / 180d;
            var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
            var contacts = Contacts(frame, localX, cosine, sine, out problem);
            if (problem != null) return null;
            if (contacts.Count == 0)
            { problem = "该安装截面位于换相折弯处，没有可核验的向上折弯铜排支撑面。"; return null; }
            var positions = new[] { frame.InsulatorPhaseSpacing, 0d, -frame.InsulatorPhaseSpacing };
            var heights = new double[3]; var phases = new string[3];
            foreach (var contact in contacts.OrderByDescending(p => p.Value[0]))
            {
                var y = (contact.Value[0] - frame.Y - localX * sine) / cosine;
                var slot = Enumerable.Range(0, 3).Where(i => heights[i] == 0)
                    .OrderBy(i => Math.Abs(positions[i] - y)).ThenBy(i => i).First();
                var h = frame.Z + frame.DriveValue(DesignField.Height) / 2 - CeilingInsetMm - contact.Value[1] - InsulatorPreviewProfile.BracketHeight;
                if (h <= BridgeInsulatorMountPlan.MinimumBodyHeightMm || Math.Abs(y) + InsulatorPreviewProfile.Diameter / 2 > frame.DriveValue(DesignField.Width) / 2 - 20 + Tolerance)
                { problem = "换相吊装的本体高度或横向安装边界不足。"; return null; }
                positions[slot] = y; heights[slot] = h; phases[slot] = contact.Key;
            }
            return new BridgeInsulatorMountPlan(positions, heights, phases, frame.InsulatorAssemblyHeight);
        }

        private Dictionary<string, double[]> Contacts(BridgeDesignObject frame, double localX, double cosine, double sine, out string problem)
        {
            problem = null;
            var result = new Dictionary<string, double[]>();
            string incompletePhase = null;
            foreach (var phase in _members.GroupBy(m => m.Route.Phase))
            {
                var hits = new List<SupportFace>();
                foreach (var root in phase.GroupBy(m => m.Key))
                {
                    var candidates = root.SelectMany(m => m.Faces).Where(face =>
                    {
                        var x = frame.X + (localX - (face.Y - frame.Y) * sine) / cosine;
                        return x >= face.Start - Tolerance && x <= face.End + Tolerance;
                    }).ToArray();
                    if (candidates.Length == 0) continue;
                    if (candidates.Max(p => p.Y) - candidates.Min(p => p.Y) > Tolerance ||
                        candidates.Max(p => p.Top) - candidates.Min(p => p.Top) > Tolerance)
                    { problem = phase.Key + "相换相支撑面无法唯一确定。"; return result; }
                    hits.Add(candidates[0]);
                }
                if (hits.Count == 0) continue;
                if (hits.Count != frame.InsulatorPadCount + 1 || hits.Max(p => p.Y) - hits.Min(p => p.Y) > Tolerance)
                {
                    // 各相折弯起点错开时，只跳过尚未形成完整支撑面的相，不能连带拒绝其他可吊装相。
                    incompletePhase = phase.Key + "相叠排未全部进入可吊装直段。";
                    continue;
                }
                var levels = hits.Select(p => p.Top).OrderBy(v => v).ToArray();
                var thickness = phase.First().Route.SectionThickness;
                if (levels.Skip(1).Where((v, i) => Math.Abs(v - levels[i] - 2 * thickness) > Tolerance).Any())
                { problem = phase.Key + "相叠排间距与垫块厚度不一致。"; return result; }
                result.Add(phase.Key, new[] { hits[0].Y, levels.Last() });
            }
            if (result.Count == 0) problem = incompletePhase;
            return result;
        }

        private IEnumerable<SupportFace> HorizontalSupportFaces(Member member)
        {
            var path = member.Points;
            for (var i = 1; i < path.Length; i++)
            {
                var a = path[i - 1]; var b = path[i];
                if (Math.Abs(a[2] - b[2]) > Tolerance || a[2] <= member.Baseline + member.Route.SectionThickness + Tolerance) continue;
                // 本体顶面须全部落在直段；圆角和竖直折弯不能用中心线插值冒充接触面。
                var contactRadius = Math.Min(InsulatorPreviewProfile.PadWidth, member.Route.SectionWidth) / 2;
                double start, end, y;
                if (Math.Abs(a[1] - b[1]) <= Tolerance && Math.Abs(a[0] - b[0]) > Tolerance)
                {
                    var reserve = member.Radius + contactRadius;
                    start = Math.Min(a[0], b[0]) + reserve;
                    end = Math.Max(a[0], b[0]) - reserve;
                    y = a[1];
                }
                else if (Math.Abs(a[0] - b[0]) <= Tolerance && Math.Abs(a[1] - b[1]) > Tolerance)
                {
                    var halfSpan = member.Route.SectionWidth / 2 - contactRadius;
                    start = a[0] - halfSpan;
                    end = a[0] + halfSpan;
                    y = (a[1] + b[1]) / 2;
                }
                else continue;
                if (start <= end)
                    yield return new SupportFace { Start = start, End = end, Y = y, Top = a[2] + member.Route.SectionThickness / 2 };
            }
        }

        internal BridgeDesignObject[] Apply(IReadOnlyList<BridgeDesignObject> objects)
            => Apply(objects, null);

        internal BridgeDesignObject[] Apply(IReadOnlyList<BridgeDesignObject> objects, ISet<string> targets)
        {
            if (_members.Count == 0 && objects.All(o => o.MountPlan == null && o.InsulatorMountProblem == null)) return objects.ToArray();
            var replacements = new Dictionary<string, BridgeDesignObject>();
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame))
            foreach (var group in objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id)
                .GroupBy(o => o.InsulatorGroupId ?? o.Id))
            {
                var members = group.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
                if (members.Any(BridgeSmallOffsetSupportPlanner.IsSupport)) continue;
                if (targets != null && !members.Any(o => targets.Contains(o.Id))) continue;
                var plan = At(frame, members[0].X, out var problem);
                for (var i = 0; i < members.Length; i++)
                    if (plan != null || problem != null || members[i].MountPlan != null || members[i].InsulatorMountProblem != null)
                        replacements[members[i].Id] = members[i].WithMountPlan(plan, 2 - i, problem);
            }
            return objects.Select(o => replacements.TryGetValue(o.Id, out var changed) ? changed : o).ToArray();
        }
    }
}
