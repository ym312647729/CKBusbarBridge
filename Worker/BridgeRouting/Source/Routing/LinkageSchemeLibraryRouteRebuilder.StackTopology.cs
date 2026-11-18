using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        private static bool TryBuildHistoricalStackPlacementEvidence(LinkageHistoricalStackContext context,
            IList<DirectPlacedRoute> traversals, out LinkageHistoricalStackPlacementEvidence evidence,
            out string diagnostic)
        {
            evidence = new LinkageHistoricalStackPlacementEvidence { Context = context };
            diagnostic = "历史叠排证据缺少完整源身份或同源输出地址不一致。";
            if (context.Sections.Count == 0 || context.Sections.Any(section => section == null ||
                    string.IsNullOrWhiteSpace(section.SectionKey) || !Enum.IsDefined(typeof(LinkageHistoricalStackSectionKind), section.Kind)) ||
                context.Sections.Select(section => section.SectionKey).Distinct().Count() != context.Sections.Count) return false;
            foreach (var traversal in traversals)
            {
                var originalSources = traversal.SourceRoute.OrderedMembers
                    .Where(source => source.PositionType == MiddlePositionType).ToList();
                if (originalSources.Count != traversal.OutputPartIndices.Count) return false;
                for (var sourceIndex = 0; sourceIndex < originalSources.Count; sourceIndex++)
                {
                    var source = originalSources[sourceIndex];
                    if (string.IsNullOrWhiteSpace(source.MemberKey)) return false;
                    var mapped = new LinkageHistoricalPlacedSource
                    {
                        MemberKey = source.MemberKey, SourceBusbarIndex = source.SourceBusbarIndex,
                        GroupKey = traversal.GroupKey, MemberIndex = traversal.OutputMemberIndex,
                        PartIndex = traversal.OutputPartIndices[sourceIndex], Width = source.BusbarWidth,
                        Thickness = source.BusbarThickness, PointCount = source.CenterlinePoints.Count,
                        TouchesStart = originalSources.Take(sourceIndex).All(earlier => context.LockedBridgeMemberKeys.Contains(earlier.MemberKey)),
                        TouchesCabinet = sourceIndex == originalSources.Count - 1
                    };
                    if (evidence.Sources.TryGetValue(source.MemberKey, out var previous))
                    {
                        if (previous.SourceBusbarIndex != mapped.SourceBusbarIndex || previous.GroupKey != mapped.GroupKey ||
                            previous.MemberIndex != mapped.MemberIndex || previous.PartIndex != mapped.PartIndex ||
                            previous.Width != mapped.Width || previous.Thickness != mapped.Thickness || previous.PointCount != mapped.PointCount)
                            return false;
                        previous.TouchesStart |= mapped.TouchesStart;
                        previous.TouchesCabinet |= mapped.TouchesCabinet;
                    }
                    else evidence.Sources.Add(source.MemberKey, mapped);
                }
            }
            foreach (var sourceKey in context.Sections.SelectMany(section => section.SourceMemberKeys))
                if (sourceKey == null || !evidence.Sources.ContainsKey(sourceKey)) return false;
            diagnostic = null;
            return true;
        }
    }
}
