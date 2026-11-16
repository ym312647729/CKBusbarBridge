using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CKBusbarBridge.DesignPreview;

namespace CKBusbarBridge.Prototype
{
    // Read-only cached scene references carry source identity, never inferred engineering dimensions.
    public sealed class BridgeViewportReference
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public string SourcePath { get; }
        public string Description => DisplayName + "\n类型：" + Category + "\n来源：" + SourcePath + "\n身份：" + Id;
        internal BridgeViewportReference(BridgeMeshInstance instance)
        { Id = instance.DesignObjectId; DisplayName = instance.DisplayName; Category = instance.Category; SourcePath = instance.SourcePath; }
    }
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        internal const int ViewportReferenceTab = 5;
        public ObservableCollection<BridgeViewportReference> ViewportReferences { get; } = new ObservableCollection<BridgeViewportReference>();
        private string _selectedViewportReferenceId;
        public bool HasViewportReferences => ViewportReferences.Count != 0;
        public BridgeViewportReference SelectedViewportReference
        {
            get => ViewportReferences.FirstOrDefault(reference => reference.Id == _selectedViewportReferenceId);
            set { if (value != null) SelectViewportReference(value.Id); }
        }
        internal void UpdateViewportReferences(IEnumerable<BridgeMeshInstance> instances)
        {
            var references = instances.Where(instance => instance.Category != "hanger" && Editor.Find(instance.DesignObjectId) == null && Editor.FindCopperSection(instance.DesignObjectId) == null &&
                !instance.MemberIds.Any(id => Editor.Find(id) != null)).GroupBy(instance => instance.DesignObjectId, StringComparer.Ordinal)
                .Select(group => new BridgeViewportReference(group.First())).OrderBy(reference => reference.Id, StringComparer.Ordinal).ToArray();
            if (references.Select(reference => reference.Id).SequenceEqual(ViewportReferences.Select(reference => reference.Id))) return;
            ViewportReferences.Clear(); foreach (var reference in references) ViewportReferences.Add(reference);
            if (!ViewportReferences.Any(reference => reference.Id == _selectedViewportReferenceId)) _selectedViewportReferenceId = null;
            RaisePropertyChanged(nameof(HasViewportReferences)); RaisePropertyChanged(nameof(SelectedViewportReference)); RaisePropertyChanged(nameof(SelectionCaption));
        }
        internal void SelectViewportReference(string id)
        {
            if (HasPendingInput || !ViewportReferences.Any(reference => reference.Id == id)) return;
            _selectedViewportReferenceId = id; _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _selectedHangerId = null; Editor.SelectedId = null;
            SelectedTab = ViewportReferenceTab; RefreshFields(); RefreshSelectionBindings(); RaisePropertyChanged(nameof(SelectedViewportReference)); ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        private void SyncViewportReferenceSelection()
        {
            if (Editor.SelectedId != null || _selectedCopperSectionId != null || _selectedHangerId != null) _selectedViewportReferenceId = null;
            RaisePropertyChanged(nameof(SelectedViewportReference));
        }
    }
}
