using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        public const int CopperTab = 0;
        public const int BreakpointTab = 1;
        public const int FrameTab = 2;
        public const int InsulatorTab = 3;
        public IReadOnlyList<BridgeInsulatorObject> InsulatorObjects => Editor.InsulatorObjects;
        public string SelectedFrameInsulatorSummary => ShowFrameActions
            ? FormatFrameInsulatorPositions(InsulatorObjects.Where(item => item.FrameId == Editor.SelectedId))
            : null;

        internal static string FormatFrameInsulatorPositions(IEnumerable<BridgeInsulatorObject> items)
        {
            var summary = DescribeFrameInsulators(items);
            return summary.Positions == null ? summary.Count : summary.Count + " · " + summary.Positions;
        }

        internal static (string Count, string Positions) DescribeFrameInsulators(IEnumerable<BridgeInsulatorObject> items)
        {
            var groups = items.OrderBy(item => item.Position ?? double.MaxValue).ToArray();
            var count = groups.Length.ToString(CultureInfo.InvariantCulture) + " 套";
            if (groups.Length == 0) return (count, null);
            if (!groups.Any(item => item.Position.HasValue)) return (count, "未定位");
            return (count, string.Join(" / ", groups.Select(item => item.PositionText)) + " mm");
        }

        public BridgeInsulatorObject SelectedInsulatorObject => InsulatorObjects.FirstOrDefault(o => o.MemberIds.Contains(Editor.SelectedId));
        public bool ShowInsulatorDetails => SelectedInsulatorObject != null;
        public IReadOnlyList<KeyValuePair<string, string>> SelectedInsulatorDetails
        {
            get
            {
                var selected = SelectedInsulatorObject;
                if (selected == null) return Array.Empty<KeyValuePair<string, string>>();
                return new[]
                {
                    new KeyValuePair<string, string>("所在基架", selected.FrameLabel),
                    new KeyValuePair<string, string>("绝缘子个数", selected.Count.ToString(CultureInfo.InvariantCulture) + " 个"),
                    new KeyValuePair<string, string>("状态", selected.Error ?? "有效")
                };
            }
        }
        public string SelectedInsulatorId
        {
            get => SelectedInsulatorObject?.Id;
            set { if (value != null) Select(value); }
        }
        private bool IsInsulatorSelection => SelectedObject?.Kind == DesignObjectKind.Insulator || SelectedObject?.Kind == DesignObjectKind.FixedInsulator;
        private string SelectedObjectLabel => SelectedInsulatorObject?.Name ?? (IsInsulatorSelection
            ? SelectedObject.Name.Replace("组", "").Replace("支架装配体", "") : SelectedObject?.DisplayName);
        private string InsulatorSelectionSummary => SelectedInsulatorObject == null ? SelectedObjectLabel + "\n固定绝缘子，只读。" :
            SelectedInsulatorObject.Name + "\n所在基架：" + SelectedInsulatorObject.FrameLabel + "\n绝缘子个数：" +
            SelectedInsulatorObject.Count.ToString(CultureInfo.InvariantCulture) + " 个";

        internal BridgeDesignObject DesignFrameFor(BridgeDesignObject node)
        {
            if (node?.Kind == DesignObjectKind.Frame) return node;
            var parent = node?.Kind == DesignObjectKind.Insulator ? Editor.Find(node.ParentId) : null;
            return parent?.Kind == DesignObjectKind.Frame ? parent : null;
        }

        private void SelectObjectTab(BridgeDesignObject node)
        {
            if (node == null) return;
            if (node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator)
                SupportTabIndex = 0;
            if (node.Kind == DesignObjectKind.Frame) SelectedTab = FrameTab;
            else if (node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator) SelectedTab = InsulatorTab;
            else if (node.IsJointGroup) SelectedTab = BreakpointTab;
        }

        private void AddInsulatorFields(ICollection<BridgeDesignFieldVM> fields, BridgeDesignObject node)
        {
            var item = SelectedInsulatorObject;
            if (item == null) return;
            var recommended = Editor.RecommendedInsulator(node.Id);
            var readOnly = item.Error != null || !Editor.CanDragObject(node.Id, false);
            var position = new BridgeDesignFieldVM(node.Id, DesignField.InsulatorPosition, CommitField, PendingChanged,
                "基架内定位 (mm)", DisplayInsulatorDimension(item.Position), DisplayInsulatorDimension(recommended?.Position), readOnly)
                { SourceRevision = Editor.Revision };
            fields.Add(position);
            fields.Add(InsulatorDistanceField(item, recommended, node.Id, "距前绝缘子距离 (mm)", readOnly));
        }

        private void AddFrameInsulatorFields(ICollection<BridgeDesignFieldVM> fields, string frameId)
        {
            var index = 0;
            foreach (var item in InsulatorObjects.Where(item => item.FrameId == frameId)
                         .OrderBy(item => item.Position ?? double.MaxValue))
            {
                var member = Editor.JointRepresentative(item.Id);
                if (member == null) continue;
                var memberId = member.Id;
                var number = (++index).ToString(CultureInfo.InvariantCulture);
                var recommended = Editor.RecommendedInsulator(memberId);
                var protectedSupport = BridgeSmallOffsetSupportPlanner.IsSupport(member);
                var readOnly = !item.Position.HasValue || Editor.IsLocked(memberId) || protectedSupport;
                var position = new BridgeDesignFieldVM(memberId, DesignField.InsulatorPosition, CommitField, PendingChanged,
                    "绝缘子定位" + number + " (mm)", DisplayInsulatorDimension(item.Position),
                    DisplayInsulatorDimension(recommended?.Position), readOnly);
                if (!protectedSupport)
                    position.RemoveInsulatorCommand = new BridgeDesignCommand(() => Run(Editor.DeleteInsulator(memberId)),
                        () => ShowFrameActions && Editor.SelectedId == frameId && !HasPendingInput &&
                            Editor.Find(memberId)?.ParentId == frameId &&
                            !Editor.Affected(memberId, false).Any(node => Editor.IsLocked(node.Id)));
                fields.Add(position);
                if (item.DistanceFromPrevious.HasValue)
                    fields.Add(InsulatorDistanceField(item, recommended, memberId,
                        "绝缘子" + number + "距前距离 (mm)", readOnly || item.Error != null || !Editor.CanDragObject(memberId, false)));
            }
        }

        private BridgeDesignFieldVM InsulatorDistanceField(BridgeInsulatorObject item, BridgeInsulatorObject recommended,
            string memberId, string label, bool readOnly) =>
            new BridgeDesignFieldVM(memberId, DesignField.InsulatorDistanceFromPrevious, CommitField, PendingChanged,
                label, DisplayInsulatorDimension(item.DistanceFromPrevious),
                DisplayInsulatorDimension(recommended?.DistanceFromPrevious), readOnly || !item.DistanceFromPrevious.HasValue)
                { SourceRevision = Editor.Revision, ReferenceObjectId = item.PreviousId };

        private string DisplayInsulatorDimension(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    }
}
