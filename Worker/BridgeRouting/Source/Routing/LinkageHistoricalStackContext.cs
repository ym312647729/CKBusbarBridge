using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>Candidate-local topology evidence, deliberately outside all transport DTOs.</summary>
    public sealed class LinkageHistoricalStackContext
    {
        public IReadOnlyList<LinkageHistoricalStackSectionEvidence> Sections { get; }
        public IReadOnlyList<LinkageHistoricalInterfaceEvidence> Interfaces { get; }
        public IReadOnlyList<string> LockedBridgeMemberKeys { get; }

        public LinkageHistoricalStackContext(IEnumerable<LinkageHistoricalStackSectionEvidence> sections,
            IEnumerable<LinkageHistoricalInterfaceEvidence> interfaces = null, IEnumerable<string> lockedBridgeMemberKeys = null)
        {
            Sections = new ReadOnlyCollection<LinkageHistoricalStackSectionEvidence>(
                (sections ?? throw new ArgumentNullException(nameof(sections))).ToList());
            Interfaces = new ReadOnlyCollection<LinkageHistoricalInterfaceEvidence>(
                (interfaces ?? Enumerable.Empty<LinkageHistoricalInterfaceEvidence>()).ToList());
            LockedBridgeMemberKeys = new ReadOnlyCollection<string>(
                (lockedBridgeMemberKeys ?? Enumerable.Empty<string>()).ToList());
        }
    }

    public sealed class LinkageHistoricalInterfaceEvidence
    {
        public string FirstMemberKey { get; }
        public string SecondMemberKey { get; }
        public int FirstSegmentIndex { get; }
        public int SecondSegmentIndex { get; }
        public IReadOnlyList<LinkageSchemeLibraryVector> CommonPolygon { get; }

        public LinkageHistoricalInterfaceEvidence(string firstMemberKey, int firstSegmentIndex,
            string secondMemberKey, int secondSegmentIndex, IEnumerable<LinkageSchemeLibraryVector> commonPolygon = null)
        {
            FirstMemberKey = firstMemberKey; SecondMemberKey = secondMemberKey;
            FirstSegmentIndex = firstSegmentIndex; SecondSegmentIndex = secondSegmentIndex;
            CommonPolygon = new ReadOnlyCollection<LinkageSchemeLibraryVector>(
                (commonPolygon ?? Enumerable.Empty<LinkageSchemeLibraryVector>())
                .Select(point => point == null ? null : new LinkageSchemeLibraryVector { X = point.X, Y = point.Y, Z = point.Z }).ToList());
        }
    }

    public enum LinkageHistoricalStackSectionKind { StartLap, MiddleLap, CabinetTopLap }

    public sealed class LinkageHistoricalStackSectionEvidence
    {
        public string SectionKey { get; }
        public LinkageHistoricalStackSectionKind Kind { get; }
        public IReadOnlyList<string> SourceMemberKeys { get; }

        public LinkageHistoricalStackSectionEvidence(string sectionKey, LinkageHistoricalStackSectionKind kind,
            IEnumerable<string> sourceMemberKeys)
        {
            SectionKey = sectionKey;
            Kind = kind;
            SourceMemberKeys = new ReadOnlyCollection<string>(
                (sourceMemberKeys ?? throw new ArgumentNullException(nameof(sourceMemberKeys))).ToList());
        }
    }

    /// <summary>Output addresses, not object references: copied/adjusted outputs are rechecked.</summary>
    internal sealed class LinkageHistoricalStackPlacementEvidence
    {
        internal LinkageHistoricalStackContext Context { get; set; }
        internal Dictionary<string, LinkageHistoricalPlacedSource> Sources { get; } =
            new Dictionary<string, LinkageHistoricalPlacedSource>(StringComparer.Ordinal);

        internal bool HasExpectedSections(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest requestGroup, LinkageGroupRouteSolution outputGroup)
        {
            var groupSources = Sources.Values.Where(source => string.Equals(source.GroupKey,
                outputGroup.GroupKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var outputAddresses = outputGroup.StackMembers.SelectMany(member => member.PlanarParts
                .Select((part, index) => Tuple.Create(member.MemberIndex, index))).ToList();
            var sourceAddresses = groupSources.Select(source => Tuple.Create(source.MemberIndex, source.PartIndex)).ToList();
            if (sourceAddresses.Count != outputAddresses.Count || sourceAddresses.Distinct().Count() != sourceAddresses.Count ||
                !sourceAddresses.All(outputAddresses.Contains)) return false;
            foreach (var source in groupSources)
            {
                var actual = outputGroup.StackMembers.Single(member => member.MemberIndex == source.MemberIndex)
                    .PlanarParts[source.PartIndex];
                if (actual == null || Math.Abs(actual.BusBarWidth - source.Width) > 1e-6 ||
                    Math.Abs(actual.BusBarThickness - source.Thickness) > 1e-6 ||
                    actual.PointList?.Count != source.PointCount) return false;
            }
            var groupKeys = new HashSet<string>(groupSources.Select(source => source.MemberKey), StringComparer.Ordinal);
            var coveredKeys = new HashSet<string>(StringComparer.Ordinal);
            var bridgeKeys = new HashSet<string>(Context.LockedBridgeMemberKeys.Where(groupKeys.Contains), StringComparer.Ordinal);
            var sections = Context.Sections.Where(section => section.SourceMemberKeys.Any(groupKeys.Contains)).ToList();
            if (sections.Count == 0) return false;
            foreach (var section in sections)
            {
                if (section.SourceMemberKeys.Count == 0 || section.SourceMemberKeys.Distinct().Count() != section.SourceMemberKeys.Count ||
                    !section.SourceMemberKeys.All(groupKeys.Contains) || section.SourceMemberKeys.Any(bridgeKeys.Contains)) return false;
                var expectedCount = section.Kind == LinkageHistoricalStackSectionKind.StartLap
                    ? requestGroup.GetStartLapStackCount(request)
                    : section.Kind == LinkageHistoricalStackSectionKind.CabinetTopLap
                        ? requestGroup.GetCabinetTopLapStackCount(request) : requestGroup.GetStackCount(request);
                if (section.SourceMemberKeys.Count != expectedCount) return false;
                foreach (var memberKey in section.SourceMemberKeys)
                {
                    var source = Sources[memberKey];
                    if (section.Kind == LinkageHistoricalStackSectionKind.StartLap && !source.TouchesStart ||
                        section.Kind == LinkageHistoricalStackSectionKind.CabinetTopLap && !source.TouchesCabinet ||
                        section.Kind == LinkageHistoricalStackSectionKind.MiddleLap && (source.TouchesStart || source.TouchesCabinet))
                        return false;
                }
                coveredKeys.UnionWith(section.SourceMemberKeys);
            }
            coveredKeys.UnionWith(bridgeKeys);
            return coveredKeys.SetEquals(groupKeys);
        }
    }

    internal sealed class LinkageHistoricalPlacedSource
    {
        internal string MemberKey, GroupKey;
        internal int SourceBusbarIndex, MemberIndex, PartIndex, PointCount;
        internal double Width, Thickness;
        internal bool TouchesStart, TouchesCabinet;
    }
}
