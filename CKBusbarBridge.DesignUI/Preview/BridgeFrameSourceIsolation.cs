using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;
namespace CKBusbarBridge.Prototype
{
    internal static class BridgeFrameSourceIsolation
    {
        internal static BridgeDesignSample NormalizeDraftNames(BridgeDesignSample sample)
        {
            var groups = BusbarBridgeDraftEditor.FramesInInstallationOrder(sample.Objects.Where(node => node.Kind == DesignObjectKind.Frame))
                .GroupBy(node => node.SourceName).ToArray();
            var independent = new HashSet<string>(sample.SavedFrameLengthEditing?.IndependentFrames ?? Array.Empty<string>());
            Func<IGrouping<string, BridgeDesignObject>, bool> automatic = group => group.Key != null &&
                group.Key.StartsWith("基架", StringComparison.Ordinal) && int.TryParse(group.Key.Substring(2), out _) &&
                group.All(frame => frame.ComponentBinding == null && !independent.Contains(frame.Id));
            var reserved = new HashSet<string>(groups.Where(group => !automatic(group)).Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, string>(); var number = 1;
            foreach (var group in groups.Where(automatic))
            {
                string name;
                do { name = "基架" + (number++).ToString(CultureInfo.InvariantCulture); } while (!reserved.Add(name));
                foreach (var frame in group) replacements[frame.Id] = name;
            }
            return sample.WithGeometry(sample.Objects.Select(node => replacements.TryGetValue(node.Id, out var name) ? node.WithSourceName(name) : node), sample.Routes);
        }
        private static bool SameSpecialShape(BridgeDesignObject a, BridgeDesignObject b)
        {
            if (a.SmallOffsetPreview != null || b.SmallOffsetPreview != null)
                return a.SmallOffsetPreview != null && b.SmallOffsetPreview != null &&
                    a.SmallOffsetPreview.InletDirection == b.SmallOffsetPreview.InletDirection &&
                    a.SmallOffsetPreview.OutletDirection == b.SmallOffsetPreview.OutletDirection &&
                    Math.Abs(a.SmallOffsetPreview.InletSize - b.SmallOffsetPreview.InletSize) <= 1.0f &&
                    Math.Abs(a.SmallOffsetPreview.OutletSize - b.SmallOffsetPreview.OutletSize) <= 1.0f;
            if (a.CornerPreview != null || b.CornerPreview != null)
                return a.CornerPreview != null && b.CornerPreview != null && a.CornerPreview.ClosedEnd == b.CornerPreview.ClosedEnd &&
                    a.CornerPreview.ClosedSide == b.CornerPreview.ClosedSide;
            if (a.WallFramePreview != null || b.WallFramePreview != null)
                return a.WallFramePreview != null && b.WallFramePreview != null &&
                    Math.Abs(a.WallFramePreview.InletOffset - b.WallFramePreview.InletOffset) <= 1.0f &&
                    Math.Abs(a.WallFramePreview.InletHeight - b.WallFramePreview.InletHeight) <= 1.0f &&
                    Math.Abs(a.WallFramePreview.InletWidth - b.WallFramePreview.InletWidth) <= 1.0f;
            return true;
        }

        internal static BridgeDesignSample Apply(BridgeDesignSample target)
        {
            var replacements = new Dictionary<string, BridgeDesignObject>();
            var provider = new PrototypeBusbarBridgeDesignProvider();
            var frames = target.Objects.Where(frame => frame.Kind == DesignObjectKind.Frame).ToArray();
            var sourceNames = new HashSet<string>(frames.Select(frame => frame.SourceName), StringComparer.OrdinalIgnoreCase);
            var nextSourceNumber = sourceNames.Select(sourceName => sourceName != null && sourceName.StartsWith("基架", StringComparison.Ordinal) &&
                int.TryParse(sourceName.Substring(2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceNumber)
                    ? sourceNumber : 0).DefaultIfEmpty(0).Max() + 1;
            foreach (var group in frames.OrderBy(frame => frame.FramePositionIndex).GroupBy(frame => frame.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                var sourceModels = new List<Tuple<BridgeDesignObject, string>>();
                var hasInternalName = group.Any(frame => string.Equals(group.Key, "修复-" + frame.Id, StringComparison.Ordinal));
                foreach (var frame in group.OrderByDescending(frame => frame.ComponentBinding != null).ThenBy(frame => frame.FramePositionIndex))
                {
                    // 同一来源中按实际模型分组；位置或实例Id不同不会拆成不同名称。
                    // 不跨原来源合并，保留用户主动独立的模型。
                    var sourceModel = sourceModels.FirstOrDefault(candidate =>
                        provider.SameFrameModel(candidate.Item1, frame, target.Objects) && SameSpecialShape(candidate.Item1, frame));
                    if (sourceModel == null)
                    {
                        var sourceName = group.Key;
                        if (sourceModels.Count > 0 || hasInternalName)
                        {
                            do { sourceName = "基架" + (nextSourceNumber++).ToString(CultureInfo.InvariantCulture); }
                            while (!sourceNames.Add(sourceName));
                        }
                        sourceModel = Tuple.Create(frame, sourceName);
                        sourceModels.Add(sourceModel);
                    }
                    if (!string.Equals(frame.SourceName, sourceModel.Item2, StringComparison.Ordinal))
                        replacements[frame.Id] = frame.WithSourceName(sourceModel.Item2);
                }
            }
            return target.WithGeometry(target.Objects.Select(o => replacements.TryGetValue(o.Id, out var replacement) ? replacement : o), target.Routes);
        }

    }
}
