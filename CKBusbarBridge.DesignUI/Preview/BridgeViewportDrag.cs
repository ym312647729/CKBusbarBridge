using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal static class BridgeDragProjection
    {
        internal static bool CrossedThreshold(float x, float y, float startX, float startY)
            => (x - startX) * (x - startX) + (y - startY) * (y - startY) > 9f;
        internal static float? AxisPosition(ViewportRay ray, ViewportVector origin, ViewportVector unitAxis)
        {
            var alignment = ViewportVector.Dot(ray.Direction, unitAxis);
            var denominator = 1f - alignment * alignment;
            if (denominator <= 0f) return null;
            var offset = ray.Origin - origin;
            return (ViewportVector.Dot(offset, unitAxis) - alignment * ViewportVector.Dot(offset, ray.Direction)) / denominator;
        }
    }
    internal sealed partial class BridgeOpenGlViewport
    {
        private string _dragId;
        private float _mouseStartX, _mouseStartY, _dragOriginalX, _dragAxisStart, _dragDelta;
        private ViewportVector _dragOrigin, _dragAxis;
        private bool _dragActive;
        private long _dragRevision;
        private Dictionary<DesignField, double> _dragChanges;
        private readonly Dictionary<string, ViewportVector> _dragInstanceAxes = new Dictionary<string, ViewportVector>(StringComparer.Ordinal);
        private readonly Dictionary<string, ViewportVector> _dragObjectAxes = new Dictionary<string, ViewportVector>(StringComparer.Ordinal);
        private readonly BusbarBridgeDraftPreviewSceneFactory _sceneGeometry = new BusbarBridgeDraftPreviewSceneFactory();

        private BridgeCoordinateFrame DraftCoordinates => _viewModel.Editor.CurrentSample.Coordinates ??
            BridgeCoordinateFrame.Facing(_viewModel.Editor.CurrentSample.PreviewOriginX.Value, _viewModel.Editor.CurrentSample.PreviewOriginZ.Value);
        private static ViewportVector Vector(System.Collections.Generic.IReadOnlyList<double> values) => new ViewportVector((float)values[0], (float)values[1], (float)values[2]);
        private ViewportVector ObjectWorldPosition(BridgeDesignObject node)
            => Vector(DraftCoordinates.ToAssembly(_sceneGeometry.WorldPosition(node, _viewModel.Objects)));
        private ViewportVector ObjectDragAxis(BridgeDesignObject node)
        {
            var parent = _viewModel.Editor.Find(node.ParentId);
            var angle = parent == null ? 0f : (float)parent.ModelDirection * (float)Math.PI / 180f;
            // Editor X is the copper path station; insulator X belongs to its frame's length axis.
            return Vector(DraftCoordinates.ToAssemblyDirection(new[] { Math.Cos(angle), Math.Sin(angle), 0d })).Unit;
        }
        private void PrepareDrag(string id, float x, float y)
        {
            if (_viewModel == null || _viewModel.HasPendingInput || IsStale || id == null || !_viewModel.Editor.CanDragObject(id, _viewModel.SameSource)) return;
            var node = _viewModel.Editor.Find(id);
            _dragOrigin = ObjectWorldPosition(node); _dragAxis = ObjectDragAxis(node);
            var position = BridgeDragProjection.AxisPosition(ViewportMatrix.ScreenRay(_inverseViewProjection, _viewport, x, y), _dragOrigin, _dragAxis);
            if (!position.HasValue) { _viewModel.Message = "当前视线与拖动轴重合，请调整视角。"; return; }
            _dragId = id; _mouseStartX = x; _mouseStartY = y; _dragOriginalX = (float)node.X;
            _dragAxisStart = position.Value; _dragRevision = _viewModel.Editor.Revision;
            var affected = _viewModel.Editor.Affected(id, node.Kind != DesignObjectKind.Insulator && _viewModel.SameSource).ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var target in affected.Values) _dragObjectAxes[target.Id] = ObjectDragAxis(target);
            foreach (var instance in _frame.Instances)
            {
                var member = affected.ContainsKey(instance.DesignObjectId) ? instance.DesignObjectId : instance.MemberIds.FirstOrDefault(affected.ContainsKey);
                if (member != null) _dragInstanceAxes[instance.Id] = ObjectDragAxis(affected[member]);
            }
        }
        private bool MoveDrag(float x, float y)
        {
            if (!_objectPressed || _dragId == null) return _objectPressed;
            if (_viewModel.Editor.Revision != _dragRevision || IsStale) { CancelObjectInput(); return true; }
            if (!_dragActive)
            {
                if (!BridgeDragProjection.CrossedThreshold(x, y, _mouseStartX, _mouseStartY)) return true;
                if (_viewModel.HasPendingInput) { CancelObjectInput(); return true; }
                _dragActive = true; _viewModel.SetDragging(true);
            }
            var position = BridgeDragProjection.AxisPosition(ViewportMatrix.ScreenRay(_inverseViewProjection, _viewport, x, y), _dragOrigin, _dragAxis);
            if (!position.HasValue) return true;
            var target = _dragOriginalX + position.Value - _dragAxisStart;
            // This is the existing unprotected editor constraint boundary; no planner or host call occurs here.
            _dragChanges = _viewModel.Editor.ConstrainDragValues(_dragId, new Dictionary<DesignField, double> { [DesignField.X] = target }, _viewModel.SameSource);
            _dragDelta = _dragChanges.TryGetValue(DesignField.X, out var constrained) ? (float)constrained - _dragOriginalX : 0f;
            ScheduleRedraw(); return true;
        }
        private void ApplyDragTransform(BridgeMeshInstance instance)
        {
            if (!_dragActive || !_dragInstanceAxes.TryGetValue(instance.Id, out var axis)) return;
            var offset = axis * _dragDelta; Gl.glTranslatef(offset.X, offset.Y, offset.Z);
        }
        private void CompleteDrag()
        {
            var id = _dragId; var changes = _dragChanges; var active = _dragActive && _dragDelta != 0f;
            CancelObjectInput();
            if (active && id != null && changes?.Count > 0) _viewModel.CommitDrag(id, changes);
        }
    }
}
