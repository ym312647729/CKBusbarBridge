using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal enum BridgeViewportDisplayStyle { Wireframe, HiddenLinesRemoved, HiddenLinesVisible, Shaded, ShadedWithEdges }

    internal sealed partial class BridgeOpenGlViewport
    {
        private BridgeViewportDisplayStyle _displayStyle;
        private bool DrawSurface => _displayStyle == BridgeViewportDisplayStyle.Shaded || _displayStyle == BridgeViewportDisplayStyle.ShadedWithEdges;
        private bool DrawEdges => _displayStyle != BridgeViewportDisplayStyle.Shaded;
        private void PrepareDisplayDepth(ContextResources resources, System.Collections.Generic.IReadOnlyList<BridgeMeshInstance> instances)
        {
            if (_displayStyle == BridgeViewportDisplayStyle.Wireframe) { Gl.glDisable(0x0b71); return; }
            Gl.glDepthFunc(0x0203); // LEQUAL, including the edge of its own surface.
            Gl.glEnable(0x8037); Gl.glPolygonOffset(1f, 1f);
            if (DrawSurface) return;
            // Hidden-line modes need an invisible surface pass before any edge is drawn.
            Gl.glColorMask(0, 0, 0, 0);
            try
            {
                foreach (var instance in instances)
                {
                    if (!IsInstanceVisible(instance) || instance.OutlineOnly || BridgeViewportAppearance.Opacity(instance, _viewModel?.SelectedTab) < 1f) continue;
                    Gl.glPushMatrix(); ApplyDragTransform(instance); Gl.glMultMatrixf(instance.Transform);
                    try { Gl.glCallList(resources.Mesh(instance)); } finally { Gl.glPopMatrix(); }
                }
            }
            finally { Gl.glColorMask(1, 1, 1, 1); }
        }
        private void DrawHiddenEdges(uint mesh, float opacity)
        {
            if (_displayStyle != BridgeViewportDisplayStyle.HiddenLinesVisible) return;
            Gl.glDepthMask(0); Gl.glDepthFunc(0x0204); // GREATER: only occluded edges.
            Gl.glColor4f(.65f, .65f, .65f, opacity); Gl.glLineWidth(1f);
            Gl.glEnable(0x0b24); Gl.glLineStipple(1, 0x00ff); Gl.glCallList(mesh + 1); Gl.glDisable(0x0b24);
            Gl.glDepthFunc(0x0203);
        }
    }
}
