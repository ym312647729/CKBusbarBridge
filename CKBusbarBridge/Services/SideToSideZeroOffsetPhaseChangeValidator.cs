using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Prototype
{
    internal sealed class SideToSideZeroOffsetPhaseChangeValidator
    {
        private readonly SideToSideZeroOffsetPhaseChangeReadback _reader = new SideToSideZeroOffsetPhaseChangeReadback();

        internal string[] InspectCopper(BridgeDesignSample sample, BridgeBusinessObservation observation, BridgeUpstreamSnapshot upstream)
        {
            _reader.RequireRoles(sample);
            var problems = new List<string>();
            var parts = observation.Copper.OrderBy(c => c.Binding.Instance, StringComparer.Ordinal).ToArray();
            if (parts.Length == 0) return new[] { "侧对侧不偏移换相缺少实际铜排。" };
            if (parts.GroupBy(c => c.Binding.Instance, StringComparer.Ordinal).Any(g => g.Count() != 1))
                return new[] { "侧对侧不偏移换相实际铜排实例重复。" };
            var routes = parts.Select(p => sample.Routes.Where(r => r.ComponentBinding?.Instance == p.Binding.Instance).ToArray()).ToArray();
            if (routes.Any(r => r.Length != 1) || sample.Routes.Any(r => !parts.Any(p => p.Binding.Instance == r.ComponentBinding?.Instance)))
                return new[] { "侧对侧不偏移换相实际铜排与业务路线未逐件唯一绑定。" };
            var graph = parts.Select(p => new List<int>()).ToArray();
            for (var i = 0; i < parts.Length; i++) for (var j = i + 1; j < parts.Length; j++)
            {
                if (!_reader.Touches(parts[i], parts[j], sample.Coordinates)) continue;
                if (routes[i][0].Phase != routes[j][0].Phase)
                {
                    problems.Add("侧对侧不偏移换相不同相位存在实际接触：" + parts[i].Binding.Instance + " / " + parts[j].Binding.Instance);
                    continue;
                }
                graph[i].Add(j); graph[j].Add(i);
            }
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var indices = Enumerable.Range(0, parts.Length).Where(i => routes[i][0].Phase == phase).ToArray();
                foreach (var section in sample.Routes.Where(r => r.Phase == phase && r.GenerationGroup == "中间排").GroupBy(r => r.CopperSectionId))
                {
                    var members = new HashSet<string>(section.Select(r => (r.CopyNativePreview().GroupKey ?? "").Split('/')[0]), StringComparer.Ordinal);
                    if (!members.SetEquals(Enumerable.Range(1, upstream.SideToSide.Copper.RootCount).Select(n => phase + "-" + n)))
                        problems.Add("侧对侧不偏移换相" + phase + "相中间排叠排成员不完整：" + section.Key);
                }
                foreach (var side in new[] { "B", "A" })
                {
                    var seeds = indices.Where(i => routes[i][0].GenerationGroup == side + "柜连接段").ToArray();
                    if (seeds.Length != upstream.SideToSide.Copper.RootCount)
                        problems.Add("侧对侧不偏移换相" + phase + "相" + side + "柜连接排叠排数量不符。");
                    var visited = new HashSet<int>(seeds); var queue = new Queue<int>(seeds);
                    while (queue.Count > 0) foreach (var next in graph[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
                    if (indices.Length == 0 || seeds.Length == 0 || indices.Any(i => !visited.Contains(i)))
                        problems.Add("侧对侧不偏移换相" + phase + "相实际铜排未贯通到" + side + "柜连接段。");
                    problems.AddRange(InspectCabinetContacts(sample, observation, upstream, side, phase, seeds.Select(i => parts[i]).ToArray()));
                }
                if (!indices.Any(i => routes[i][0].GenerationGroup == "中间排"))
                    problems.Add("侧对侧不偏移换相" + phase + "相缺少中间排。");
            }
            return problems.Distinct().ToArray();
        }

        private IEnumerable<string> InspectCabinetContacts(BridgeDesignSample sample, BridgeBusinessObservation observation,
            BridgeUpstreamSnapshot upstream, string side, string phase, BridgeReadbackCopper[] seeds)
        {
            var sources = observation.CabinetCopper.Where(c => c.Side == side && c.Phase == phase).ToArray();
            var label = "侧对侧不偏移换相" + phase + "相" + side + "柜";
            if (sources.Length != upstream.SideToSide.Copper.RootCount - 1 ||
                sources.GroupBy(s => s.Copper.Binding.Instance).Any(g => g.Count() != 1))
            {
                yield return label + "实际伸出排身份或E=N−1数量不符。";
                yield break;
            }
            foreach (var source in sources)
                if (!seeds.Any(seed => _reader.Touches(seed, source.Copper, sample.Coordinates)))
                    yield return label + "伸出排未与连接排实际搭接：" + source.Copper.Binding.Instance;
            foreach (var seed in seeds)
            {
                if (!sources.Any(source => _reader.Touches(seed, source.Copper, sample.Coordinates)))
                    yield return label + "连接排未与伸出排实际搭接：" + seed.Binding.Instance;
                foreach (var other in observation.CabinetCopper.Where(c => c.Side == side && c.Phase != phase))
                    if (_reader.Touches(seed, other.Copper, sample.Coordinates))
                        yield return label + "连接排与其他相位伸出排实际接触：" + seed.Binding.Instance + " / " + other.Copper.Binding.Instance;
            }
        }

        internal string[] InspectFrames(BridgeDesignSample sample, double[] start, double[] end)
        {
            var frames = sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            var problems = new List<string>();
            if (frames.Length == 0) return new[] { "侧对侧不偏移换相缺少基架。" };
            var previous = start;
            foreach (var frame in frames)
            {
                if (frame.FrameTemplateKey != "SideToSidePhaseChange029")
                    problems.Add("侧对侧不偏移换相基架不是029换相模板：" + frame.DisplayName);
                var ports = new BridgeDraftGenerationPlanFactory().InterfacePoints(frame, sample.Coordinates);
                if (previous != null && BridgeBusinessGeometry.Distance(previous, ports[0]) > .15)
                    problems.Add("侧对侧不偏移换相基架入口未与前序实际接口贯通：" + frame.DisplayName);
                previous = ports[1];
            }
            if (end != null && BridgeBusinessGeometry.Distance(previous, end) > .15)
                problems.Add("侧对侧不偏移换相末端基架未与A柜基座接口贯通。");
            return problems.ToArray();
        }

        internal void RequireGeneratedCopper(BridgeDesignSample target, BridgeBusinessObservation actual)
        {
            _reader.RequireRoles(target);
            var units = AlgorithmBusbarGenerationService.DescribeBusinessParts(target).ToList();
            foreach (var part in actual.Copper)
            {
                var path = BridgeCopperBusinessRecovery.Compact(part.Path);
                var candidates = units.Where(u => BridgeBusinessGeometry.SamePath(BridgeCopperBusinessRecovery.Compact(u.Path), path)).ToArray();
                var matches = candidates.Where(u => ParameterDifferences(u, part).Length == 0).ToArray();
                var context = part.Binding.Instance + "；文件=" + part.Binding.Path + "；配置=" + part.Binding.Configuration;
                if (matches.Length > 1)
                    throw new InvalidOperationException("侧对侧不偏移换相提交核验：实际铜排存在多重匹配，匹配数量=" + matches.Length + "：" +
                        context + "；目标=" + string.Join(", ", matches.Select(u => u.Key).OrderBy(k => k, StringComparer.Ordinal)));
                if (matches.Length == 0)
                    throw new InvalidOperationException("侧对侧不偏移换相提交核验：实际铜排无匹配：" + context + "；" +
                        (candidates.Length > 0 ? string.Join("；", candidates.Select(u => "目标=" + u.Key + "，" +
                            string.Join("；", ParameterDifferences(u, part)))) : PathDifference(units, path)));
                units.Remove(matches[0]);
            }
            if (units.Count != 0)
                throw new InvalidOperationException("侧对侧不偏移换相提交核验：缺少目标加工铜排：" +
                    string.Join(", ", units.Select(u => u.Key).OrderBy(k => k, StringComparer.Ordinal)));
        }

        private string[] ParameterDifferences(BridgeCopperReadUnit unit, BridgeReadbackCopper actual)
        {
            var native = unit.Native; var differences = new List<string>();
            AddDifference(differences, "排宽", native.BusBarWidth, actual.Width);
            AddDifference(differences, "排厚", native.BusBarThickness, actual.Thickness);
            if (actual.Radius.HasValue) AddDifference(differences, "平弯半径", native.BarBendingRadius, actual.Radius.Value);
            // 复用生成单元的解析结果，覆盖旧路径自动识别立弯及未指定半径的沿用规则；原始方案保持不变。
            if (actual.VerticalRadius.HasValue)
                AddDifference(differences, "立弯半径", unit.EffectiveVerticalRadius ?? native.VerticalBendingRadius, actual.VerticalRadius.Value);
            else if (unit.EffectiveVerticalRadius.HasValue) differences.Add("立弯半径缺失：目标=" + Number(unit.EffectiveVerticalRadius.Value));
            if (native.IsMilledBar != (actual.Milling != null)) differences.Add("铣排类型：目标=" + native.IsMilledBar + "，实际=" + (actual.Milling != null));
            else if (actual.Milling != null)
            {
                if (actual.Milling.Length != 3) differences.Add("铣排参数数量：目标=3，实际=" + actual.Milling.Length);
                else
                {
                    AddDifference(differences, "铣排原宽", native.MilledBarOriginalWidth, actual.Milling[0]);
                    AddDifference(differences, "铣排增加宽", native.MilledBarAdditionalWidth, actual.Milling[1]);
                    AddDifference(differences, "铣排起始长度", native.MilledBarStartLength, actual.Milling[2]);
                    if (native.MilledBarFeatureSide != actual.MilledSide) differences.Add("铣排加工侧：目标=" + native.MilledBarFeatureSide + "，实际=" + actual.MilledSide);
                }
            }
            return differences.ToArray();
        }

        private void AddDifference(List<string> differences, string field, double expected, double actual)
        {
            if (!(Math.Abs(expected - actual) <= .001)) differences.Add(field + "：目标=" + Number(expected) +
                "，实际=" + Number(actual) + "，差值=" + Number(actual - expected) + " mm（容差=0.001 mm）");
        }

        private string PathDifference(IEnumerable<BridgeCopperReadUnit> units, double[][] actual)
        {
            var paths = units.Select(u => new { u.Key, Path = BridgeCopperBusinessRecovery.Compact(u.Path) }).ToArray();
            var nearest = paths.Select(u => new { u.Key, Deviation = PathDeviation(u.Path, actual) })
                .Where(u => !double.IsInfinity(u.Deviation)).OrderBy(u => u.Deviation).ThenBy(u => u.Key, StringComparer.Ordinal).FirstOrDefault();
            // 最近目标只用于报错，不能替代严格路径匹配或建立业务绑定。
            return nearest != null ? "路径无匹配；最近目标=" + nearest.Key + "，最大对应点偏差=" + Number(nearest.Deviation) + " mm（容差=0.03 mm）" :
                "路径无匹配；实际点数=" + (actual?.Length ?? 0) + "，剩余目标点数=" + string.Join(", ", paths.Select(u => u.Key + ":" + u.Path.Length));
        }

        private double PathDeviation(double[][] expected, double[][] actual)
        {
            if (expected == null || actual == null || expected.Length == 0 || expected.Length != actual.Length) return double.PositiveInfinity;
            return Math.Min(expected.Select((p, i) => BridgeBusinessGeometry.Distance(p, actual[i])).Max(),
                expected.Select((p, i) => BridgeBusinessGeometry.Distance(p, actual[actual.Length - 1 - i])).Max());
        }

        private string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
