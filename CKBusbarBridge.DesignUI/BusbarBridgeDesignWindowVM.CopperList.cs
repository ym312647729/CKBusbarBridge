using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeCopperListRow
    {
        internal BridgeCopperListRow(BridgeCopperSection section, int? rootCount, string startJointId,
            string endJointId, string jointName, double? breakpointDistance)
        {
            Section = section;
            RootCount = rootCount;
            StartJointId = startJointId;
            EndJointId = endJointId;
            JointName = jointName;
            BreakpointText = breakpointDistance?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        }

        internal BridgeCopperSection Section { get; }
        public int? Index => Section?.Index;
        public string Name => Section?.Name ?? JointName;
        public int? RootCount { get; }
        public double? Length => Section?.Length;
        public string StartJointId { get; }
        public string EndJointId { get; }
        public string JointName { get; }
        public string BreakpointText { get; }
        public bool HasStartJoint => StartJointId != null;
        public bool HasEndJoint => EndJointId != null;
        public bool HasSelectableJoint => HasStartJoint || HasEndJoint;
    }

    public sealed partial class BusbarBridgeDesignWindowVM
    {
        private IReadOnlyList<BridgeCopperListRow> _copperRows = Array.Empty<BridgeCopperListRow>();
        private long _copperRowsRevision = -1;

        public IReadOnlyList<BridgeCopperListRow> CopperRows
        {
            get
            {
                if (_copperRowsRevision == Editor.Revision) return _copperRows;
                _copperRows = BuildCopperRows();
                _copperRowsRevision = Editor.Revision;
                return _copperRows;
            }
        }

        internal BridgeCopperListRow SelectedCopperListRow => SelectedCopperSectionId is string sectionId
            ? CopperRows.FirstOrDefault(row => row.Section?.Id == sectionId)
            : Editor.SelectedId is string jointId
                ? CopperRows.FirstOrDefault(row => row.EndJointId == jointId) ??
                  CopperRows.FirstOrDefault(row => row.StartJointId == jointId)
                : null;

        private IReadOnlyList<BridgeCopperListRow> BuildCopperRows()
        {
            var sample = Editor.CurrentSample;
            if (sample == null) return Array.Empty<BridgeCopperListRow>();
            var physicalRoutes = sample.Routes.Where(route => route.IsPhysicalGeometry).ToArray();
            var bySection = physicalRoutes.Where(route => route.CopperSectionId != null)
                .ToLookup(route => route.CopperSectionId, StringComparer.Ordinal);
            // 演示路线每相记录一根；真实方案只数明确绑定到该段排的物理排件。
            var schematicCounts = Array.Empty<int>();
            if (!sample.Source.IsModelDerived && physicalRoutes.Length == 0)
            {
                var scene = new BusbarBridgeDraftPreviewSceneFactory();
                schematicCounts = sample.Routes.Where(route => route.LSegmentStartIds.Count == 0)
                    .Select(route => scene.CopperSegments(route, Objects).Count).ToArray();
            }
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<BridgeCopperListRow>();
            foreach (var section in CopperSections)
            {
                var start = SelectableJoint(section.StartPointId);
                var end = SelectableJoint(section.EndPointId);
                if (start != null) covered.Add(start.Id);
                if (end != null) covered.Add(end.Id);
                var rootCount = schematicCounts.Length == 0 ? bySection[section.Id].Count() :
                    schematicCounts.Count(count => count >= section.Index);
                var distance = end == null ? null : Editor.MeasureJoint(end.Id)?.DistanceFromPrevious;
                if (!distance.HasValue && end?.Kind == DesignObjectKind.FixedJoint)
                    distance = section.Length - sample.Parameters.OverlapLengthMm;
                rows.Add(new BridgeCopperListRow(section, rootCount > 0 ? (int?)rootCount : null, start?.Id,
                    end?.Id, end?.Name, distance));
            }
            foreach (var joint in JointObjects.Where(item => !covered.Contains(item.Id)))
                rows.Add(new BridgeCopperListRow(null, null, null, joint.Id, joint.Name,
                    Editor.MeasureJoint(joint.Id)?.DistanceFromPrevious));
            return rows.AsReadOnly();
        }

        private BridgeDesignObject SelectableJoint(string id)
        {
            var node = Editor.JointRepresentative(id);
            return node?.IsJointGroup == true && node.IsEditable ? node : null;
        }
    }
}
