using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace CKBusbarBridge.Prototype
{
    // 顺序对应模板第一、二、三组，沿模板Z正向到负向。零高度表示该组不装本体/垫块。
    public sealed class BridgeInsulatorMountPlan
    {
        internal const double MinimumBodyHeightMm = 120.627174484597;
        [JsonProperty] public IReadOnlyList<double> Transverse { get; private set; }
        [JsonProperty] public IReadOnlyList<double> BodyHeights { get; private set; }
        [JsonProperty] public IReadOnlyList<string> Phases { get; private set; }
        [JsonProperty] public double NominalAssemblyHeight { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool IsFloorStanding { get; private set; }
        public BridgeInsulatorMountPlan(IEnumerable<double> transverse, IEnumerable<double> bodyHeights,
            IEnumerable<string> phases, double nominalAssemblyHeight)
            : this(transverse, bodyHeights, phases, nominalAssemblyHeight, false) { }

        [JsonConstructor]
        public BridgeInsulatorMountPlan(IEnumerable<double> transverse, IEnumerable<double> bodyHeights,
            IEnumerable<string> phases, double nominalAssemblyHeight, bool isFloorStanding)
        {
            var y = transverse?.ToArray(); var h = bodyHeights?.ToArray(); var p = phases?.ToArray();
            if (y == null || h == null || p == null || y.Length != 3 || h.Length != 3 || p.Length != 3 ||
                y.Concat(h).Concat(new[] { nominalAssemblyHeight }).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                nominalAssemblyHeight <= 25 || h.Any(v => v != 0 && v <= MinimumBodyHeightMm) || !h.Any(v => v > 0) ||
                Enumerable.Range(0, 3).Any(i => h[i] > 0 && !new[] { "A", "B", "C" }.Contains(p[i])) ||
                p.Where((v, i) => h[i] > 0).Distinct().Count() != h.Count(v => v > 0) ||
                isFloorStanding && h.Count(v => v > 0) != 1)
                throw new ArgumentException("换相吊装需要有效的逐相位置、高度和原落底高度。");
            Transverse = Array.AsReadOnly(y); BodyHeights = Array.AsReadOnly(h); Phases = Array.AsReadOnly(p);
            NominalAssemblyHeight = nominalAssemblyHeight;
            IsFloorStanding = isFloorStanding;
        }
        public string Identity => string.Join("_", Enumerable.Range(0, 3).Select(i =>
                Phases[i] + ":" + Transverse[i].ToString("0.###", CultureInfo.InvariantCulture) + ":" +
            BodyHeights[i].ToString("0.###", CultureInfo.InvariantCulture))) + ":N" +
            NominalAssemblyHeight.ToString("0.###", CultureInfo.InvariantCulture) + (IsFloorStanding ? ":floor" : "");
    }
}
