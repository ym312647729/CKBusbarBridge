using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteLinkBar.Algorithm.Geometry
{
    // 从柜顶残隙原实现等价提取。只分区和扣除实际占位，不决定端部角色或搭接工艺。
    internal static class LinkageSupportProjectionPartition
    {
        private const double PartitionTolerance = LinkageRouteGeometryTolerances.PointCoincidence;

        internal static IEnumerable<LinkageSupportProjectionCell> Partition(
            IReadOnlyList<LinkageSupportProjectionOccupant> occupants)
        {
            var lengthBoundaries = occupants.SelectMany(occupant => new[]
                { occupant.LengthMinimum, occupant.LengthMaximum }).Distinct().OrderBy(value => value).ToArray();
            var widthBoundaries = occupants.SelectMany(occupant => new[]
                { occupant.WidthMinimum, occupant.WidthMaximum }).Distinct().OrderBy(value => value).ToArray();
            for (var lengthIndex = 0; lengthIndex + 1 < lengthBoundaries.Length; lengthIndex++)
            for (var widthIndex = 0; widthIndex + 1 < widthBoundaries.Length; widthIndex++)
            {
                var lengthMinimum = lengthBoundaries[lengthIndex];
                var lengthMaximum = lengthBoundaries[lengthIndex + 1];
                var widthMinimum = widthBoundaries[widthIndex];
                var widthMaximum = widthBoundaries[widthIndex + 1];
                if (lengthMaximum - lengthMinimum <= PartitionTolerance ||
                    widthMaximum - widthMinimum <= PartitionTolerance) continue;
                var lengthCenter = (lengthMinimum + lengthMaximum) / 2d;
                var widthCenter = (widthMinimum + widthMaximum) / 2d;
                var indices = Enumerable.Range(0, occupants.Count).Where(index =>
                    lengthCenter > occupants[index].LengthMinimum && lengthCenter < occupants[index].LengthMaximum &&
                    widthCenter > occupants[index].WidthMinimum && widthCenter < occupants[index].WidthMaximum).ToArray();
                if (indices.Length == 0) continue;
                yield return new LinkageSupportProjectionCell
                {
                    LengthMinimum = lengthMinimum, LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum, WidthMaximum = widthMaximum, OccupantIndices = indices
                };
            }
        }

        // 保持旧实现的输入区间语义：内部调用不擅自裁剪或更改容差；公开门面另做边界校验。
        internal static IReadOnlyList<Tuple<double, double>> ResidualIntervals(double minimum, double maximum,
            IEnumerable<Tuple<double, double>> occupied)
        {
            var ordered = (occupied ?? Enumerable.Empty<Tuple<double, double>>())
                .Where(interval => interval.Item2 - interval.Item1 > PartitionTolerance)
                .OrderBy(interval => interval.Item1).ToList();
            var merged = new List<Tuple<double, double>>();
            foreach (var interval in ordered)
            {
                if (merged.Count == 0 || interval.Item1 > merged[merged.Count - 1].Item2 + PartitionTolerance)
                { merged.Add(Tuple.Create(interval.Item1, interval.Item2)); continue; }
                merged[merged.Count - 1] = Tuple.Create(merged[merged.Count - 1].Item1,
                    Math.Max(merged[merged.Count - 1].Item2, interval.Item2));
            }
            var residuals = new List<Tuple<double, double>>();
            var currentMinimum = minimum;
            foreach (var interval in merged)
            {
                if (interval.Item1 - currentMinimum > PartitionTolerance)
                    residuals.Add(Tuple.Create(currentMinimum, interval.Item1));
                currentMinimum = Math.Max(currentMinimum, interval.Item2);
            }
            if (maximum - currentMinimum > PartitionTolerance) residuals.Add(Tuple.Create(currentMinimum, maximum));
            return residuals;
        }
    }

    internal sealed class LinkageSupportProjectionOccupant
    {
        internal double LengthMinimum, LengthMaximum, WidthMinimum, WidthMaximum;
    }

    internal sealed class LinkageSupportProjectionCell
    {
        internal double LengthMinimum, LengthMaximum, WidthMinimum, WidthMaximum;
        internal IReadOnlyList<int> OccupantIndices;
    }
}
