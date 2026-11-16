using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal sealed partial class BridgeOpenGlViewport
    {
        private BusbarBridgeDesignWindowVM _viewModel;
        private readonly HashSet<string> _selectedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly DispatcherTimer _redrawTimer = new DispatcherTimer(DispatcherPriority.Render);
        private bool _objectPressed;
        private readonly HashSet<string> _knownDisplayIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _visibleDisplayIds = new HashSet<string>(StringComparer.Ordinal);
        internal float LastPickMilliseconds { get; private set; }
        internal float LastDrawMilliseconds => _lastFrameMilliseconds;
        internal void BindEditor(BusbarBridgeDesignWindowVM viewModel)
        {
            if (_viewModel != null) { _viewModel.ViewChanged -= EditorViewChanged; _viewModel.PropertyChanged -= EditorStateChanged; }
            CancelObjectInput();
            _viewModel = viewModel;
            if (_viewModel != null) { _viewModel.ViewChanged += EditorViewChanged; _viewModel.PropertyChanged += EditorStateChanged; }
            EditorViewChanged(this, EventArgs.Empty);
        }
        private void EditorViewChanged(object sender, EventArgs args)
        {
            ResetDimensionHover();
            RefreshVisibleIdentities();
            _selectedIds.Clear();
            if (_viewModel != null)
            {
                if (_frame != null) _viewModel.UpdateViewportReferences(_frame.Instances);
                foreach (var id in _viewModel.SelectedFrameIds) _selectedIds.Add(id);
                if (_viewModel.SelectedObject != null) _selectedIds.Add(_viewModel.SelectedObject.Id);
                if (_viewModel.SelectedCopperSection != null) _selectedIds.Add(_viewModel.SelectedCopperSection.Id);
                if (_viewModel.SelectedHangerId != null) _selectedIds.Add(_viewModel.SelectedHangerId);
                if (_viewModel.SelectedViewportReference != null) _selectedIds.Add(_viewModel.SelectedViewportReference.Id);
            }
            ScheduleRedraw();
            RefreshMarkers();
        }
        private void EditorStateChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BusbarBridgeDesignWindowVM.SelectedTab))
            { CancelObjectInput(); RefreshMarkers(); ScheduleRedraw(); return; }
            if (args.PropertyName != nameof(BusbarBridgeDesignWindowVM.PlanningBusy) &&
                args.PropertyName != nameof(BusbarBridgeDesignWindowVM.NativePreviewBusy) &&
                args.PropertyName != nameof(BusbarBridgeDesignWindowVM.HasPendingInput)) return;
            ResetDimensionHover(); CloseIssuePopup(); ScheduleRedraw();
        }
        private void RefreshVisibleIdentities()
        {
            _knownDisplayIds.Clear(); _visibleDisplayIds.Clear();
            if (_frame == null) return;
            foreach (var instance in _frame.Instances)
            {
                var visible = IsInstanceVisible(instance);
                foreach (var id in instance.MemberIds.Concat(new[] { instance.DesignObjectId, instance.GroupId }).Where(id => id != null))
                { _knownDisplayIds.Add(id); if (visible) _visibleDisplayIds.Add(id); }
            }
        }
        private bool IsDimensionVisible(BridgePreviewDimension dimension)
        {
            if (_knownDisplayIds.Contains(dimension.ObjectId)) return _visibleDisplayIds.Contains(dimension.ObjectId);
            var node = _viewModel?.Editor.Find(dimension.ObjectId);
            return node == null || _viewModel.IsPreviewObjectVisible(node);
        }
        private bool IsSelected(BridgeMeshInstance instance) => BridgeViewportAppearance.CanSelect(instance.Category, _viewModel?.SelectedTab) &&
            (_selectedIds.Contains(instance.DesignObjectId) || instance.GroupId != null && _selectedIds.Contains(instance.GroupId) || instance.MemberIds.Any(_selectedIds.Contains));
        private bool CanEditDimension(BridgePreviewDimension dimension)
        {
            if (_viewModel == null) return true;
            var node = _viewModel.Editor.Find(dimension.ObjectId);
            var category = node == null ? _frame?.Instances.FirstOrDefault(instance => instance.DesignObjectId == dimension.ObjectId ||
                instance.GroupId == dimension.ObjectId || instance.MemberIds.Contains(dimension.ObjectId))?.Category :
                node.Kind == DesignObjectKind.Frame ? "frame" : node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator ? "insulator" : "copper";
            return BridgeViewportAppearance.CanSelect(category ?? "reference", _viewModel.SelectedTab);
        }
        private void ScheduleRedraw()
        {
            if (_stop || _redrawTimer.IsEnabled) return;
            _redrawTimer.Interval = TimeSpan.FromMilliseconds(1000f / Math.Max(1, Appearance.TargetFramesPerSecond));
            _redrawTimer.Start();
        }
        private void RedrawScheduled(object sender, EventArgs args) { _redrawTimer.Stop(); if (!_stop) IdleFrameRequested?.Invoke(); }
        private bool IsInstanceVisible(BridgeMeshInstance instance)
        {
            if (_viewModel == null) return true;
            if (instance.Phase != null && _viewModel.PhaseFilter != "全部" && instance.Phase != _viewModel.PhaseFilter) return false;
            switch (instance.Category)
            {
                case "copper": return _viewModel.ShowBusbars;
                case "frame": case "base": case "raisedBase": return _viewModel.ShowFrames;
                case "insulator": return _viewModel.ShowInsulators;
                default: return _viewModel.ShowReferences;
            }
        }
        internal void RequestRedraw() => ScheduleRedraw();
        internal ViewportBounds? SelectionBounds()
        {
            if (_viewModel?.LocatedIssue != null && _viewModel.Editor.CurrentSample != null)
            {
                var issueBounds = ViewportBounds.Empty; var found = false;
                foreach (var corner in _viewModel.LocatedIssue.CopperCorners.Concat(_viewModel.LocatedIssue.MetalCorners))
                { issueBounds.Include(Vector(DraftCoordinates.ToAssembly(corner))); found = true; }
                if (found) return issueBounds;
            }
            var selected = _frame?.Instances.Where(IsSelected).ToArray();
            if (selected == null || selected.Length == 0)
            {
                if (_viewModel?.SelectedObject == null) return null;
                var point = ObjectWorldPosition(_viewModel.SelectedObject);
                return new ViewportBounds { Minimum = point, Maximum = point };
            }
            var bounds = ViewportBounds.Empty;
            foreach (var instance in selected)
            {
                var local = instance.Shape.PickingIndex.Bounds;
                foreach (var x in new[] { local.Minimum.X, local.Maximum.X }) foreach (var y in new[] { local.Minimum.Y, local.Maximum.Y }) foreach (var z in new[] { local.Minimum.Z, local.Maximum.Z })
                    bounds.Include(ViewportMatrix.Transform(instance.Transform, new ViewportVector(x, y, z)));
            }
            return bounds;
        }
        private BridgeMeshInstance PickObject(float x, float y, bool framesOnly = false)
        {
            if (IsStale || IsPreviewOnly || _picker == null || _inverseViewProjection == null || _viewport[2] == 0 || _viewport[3] == 0) return null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var hit = _picker.Pick(ViewportMatrix.ScreenRay(_inverseViewProjection, _viewport, x, y), instance => IsInstanceVisible(instance) &&
                (BridgeViewportAppearance.Opacity(instance, _viewModel?.SelectedTab) == 1f ||
                 BridgeViewportAppearance.CanSelect(instance.Category, _viewModel?.SelectedTab) && (!framesOnly || instance.Category == "frame")));
            watch.Stop(); LastPickMilliseconds = (float)watch.Elapsed.TotalMilliseconds;
            return hit != null && BridgeViewportAppearance.CanSelect(hit.Category, _viewModel?.SelectedTab) && (!framesOnly || hit.Category == "frame") ? hit : null;
        }
        private string EditorObjectId(BridgeMeshInstance instance)
        {
            if (_viewModel?.Editor.Find(instance.DesignObjectId) != null) return instance.DesignObjectId;
            return instance.MemberIds.FirstOrDefault(id => _viewModel?.Editor.Find(id) != null);
        }
        private void SelectInstance(BridgeMeshInstance instance, bool control)
        {
            if (_viewModel == null) return;
            if (_viewModel.Editor.FindCopperSection(instance.DesignObjectId) != null) _viewModel.SelectCopperSection(instance.DesignObjectId);
            else if (instance.Category == "hanger" && _viewModel.HangerRows.Any(row => row.Id == instance.DesignObjectId))
            {
                if (!_viewModel.HasPendingInput) _viewModel.SelectedHangerId = instance.DesignObjectId;
            }
            else
            {
                var id = EditorObjectId(instance);
                if (id != null)
                {
                    if (control && _viewModel.Editor.Find(id).Kind == DesignObjectKind.Frame) _viewModel.ToggleFrameSelection(id);
                    else _viewModel.Select(id);
                }
                else if (!_viewModel.HasPendingInput)
                {
                    // Exact exported reference identity is retained even before a bridge plan exists.
                    _viewModel.SelectViewportReference(instance.DesignObjectId);
                }
            }
            ScheduleRedraw();
        }
        private bool HandleObjectMessage(int message, IntPtr buttons, IntPtr parameter)
        {
            if (message == 0x0100 && buttons.ToInt64() == 27 && _objectPressed) { CancelObjectInput(); return true; }
            var packed = parameter.ToInt64(); var x = (short)(packed & 0xffff); var y = (short)((packed >> 16) & 0xffff);
            if (message == 0x0200) UpdateIssuePopup(x, y);
            if (message == 0x0200 && _objectPressed)
            {
                if ((buttons.ToInt64() & 1) == 0) { CancelObjectInput(); return true; }
                return MoveDrag(x, y);
            }
            if (message == 0x0202 && _objectPressed) { CompleteDrag(); return true; }
            if (message != 0x0201 && message != 0x0203) return false;
            if ((buttons.ToInt64() & 0x0010) != 0) return false;
            if (SelectMarker(x, y, (buttons.ToInt64() & 8) != 0)) return true;
            var control = (buttons.ToInt64() & 8) != 0;
            var instance = control ? PickObject(x, y, true) ?? PickObject(x, y) : PickObject(x, y); if (instance == null) return false;
            SelectInstance(instance, (buttons.ToInt64() & 8) != 0);
            _objectPressed = true;
            if ((buttons.ToInt64() & 8) == 0 && message != 0x0203) PrepareDrag(EditorObjectId(instance), x, y);
            SetFocus(_messages.Handle); SetCapture(_messages.Handle); return true;
        }
        private void CancelObjectInput()
        {
            var captured = _objectPressed; _objectPressed = false;
            var active = _dragActive; _dragActive = false; _dragId = null; _dragChanges = null; _dragDelta = 0f; _dragInstanceAxes.Clear();
            _dragObjectAxes.Clear(); ResetDimensionHover(); CloseIssuePopup();
            if (active) _viewModel?.SetDragging(false);
            if (captured) ReleaseCapture();
            if (captured || active) ScheduleRedraw();
        }
    }
}
