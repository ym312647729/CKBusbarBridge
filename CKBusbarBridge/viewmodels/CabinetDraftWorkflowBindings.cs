using System;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.viewmodels
{
    internal interface ICabinetDraftWorkflow
    {
        BridgeParameterDraftDocument DraftDocument { get; }
        string ProjectPath { get; }
        CabinetDrawingNumberMetadataSession DrawingNumberSession { get; }
        BridgeParameterDraftDocument SubmitCabinetInputs();
        void CarryDraftProject(string projectPath, string drawingMetadata);
        void RestoreDraftInputs(BridgeParameterDraftDocument draft);
        void PreviewCabinetInputs(BridgeParameterDraftDocument expectedDraft, bool resetScenario);
        event EventHandler DraftPreviewChanged;
        void ReleaseDraftWindow();
    }

    public partial class CabinetDriverWindowVM : ICabinetDraftWorkflow
    {
        private BridgeParameterDraftDocument _cabinetDraftDocument;
        BridgeParameterDraftDocument ICabinetDraftWorkflow.DraftDocument => _cabinetDraftDocument;
        public event EventHandler DraftPreviewChanged;
        private void NotifyDraftPreview(object sender, EventArgs args) => DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
        void ICabinetDraftWorkflow.ReleaseDraftWindow()
        {
            if (_cabinetDraftDocument == null) return;
            _cabinetDraftDocument.Session.Changed -= NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready -= NotifyDraftPreview;
        }
        CabinetDrawingNumberMetadataSession ICabinetDraftWorkflow.DrawingNumberSession => _drawingNumberMetadataSession;
        void ICabinetDraftWorkflow.CarryDraftProject(string projectPath, string drawingMetadata)
        {
            ProjectPath = projectPath;
            if (!string.IsNullOrWhiteSpace(drawingMetadata) && !_drawingNumberMetadataSession.TryCommit(drawingMetadata, out var error))
                throw new InvalidOperationException(error);
        }
        void ICabinetDraftWorkflow.RestoreDraftInputs(BridgeParameterDraftDocument draft)
        {
            var restored = draft.Session.Parameters().ToObject<CabinetDriverParameters>();
            ProjectPath = restored.ProjectPath;
            Parameters = restored;
            if (!string.IsNullOrWhiteSpace(draft.Session.DrawingMetadata) &&
                !_drawingNumberMetadataSession.TryCommit(draft.Session.DrawingMetadata, out var error))
                throw new InvalidOperationException(error);
            RaisePropertyChanged(nameof(Parameters));
        }
        void ICabinetDraftWorkflow.PreviewCabinetInputs(BridgeParameterDraftDocument expectedDraft, bool resetScenario)
            => StartParameterPreview(false, expectedDraft, resetScenario);
        BridgeParameterDraftDocument ICabinetDraftWorkflow.SubmitCabinetInputs()
        {
            if (Parameters == null) throw new InvalidOperationException("柜体参数尚未就绪。");
            if (!TryValidateStandaloneBasePlans(Parameters, out var error, validateNominalOpeningElevation: true))
            {
                BaseParametersRequested?.Invoke(error);
                throw new InvalidOperationException(error);
            }
            return StartParameterPreview();
        }
        private BridgeParameterDraftDocument StartParameterPreview(bool confirmDrawingMetadata = true, BridgeParameterDraftDocument expectedDraft = null, bool resetScenario = false)
        {
            if (_swApp == null) throw new InvalidOperationException("未连接到 SolidWorks，无法建立柜体草稿。");
            if (confirmDrawingMetadata)
            {
                if (!_drawingNumberMetadataSession.TryEnsureCommitted(out var error)) throw new InvalidOperationException(error);
                CKBusbarBridge.Prototype.CabinetParameterPropertyCodec.DrawingInputs(_drawingNumberMetadataSession.CommittedMetadataJson);
            }
            var draft = BridgeParameterDraftDocuments.For(_swApp).Begin(Parameters, "face-to-face", null,
                _drawingNumberMetadataSession.CommittedMetadataJson, expectedDraft ?? _cabinetDraftDocument, resetScenario, confirmDocument: confirmDrawingMetadata);
            ((ICabinetDraftWorkflow)this).ReleaseDraftWindow();
            _cabinetDraftDocument = draft;
            _cabinetDraftDocument.Session.Changed += NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready += NotifyDraftPreview;
            DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
            return draft;
        }
    }
    public sealed partial class SideToSideCabinetDriverWindowVM : ICabinetDraftWorkflow
    {
        private BridgeParameterDraftDocument _cabinetDraftDocument;
        BridgeParameterDraftDocument ICabinetDraftWorkflow.DraftDocument => _cabinetDraftDocument;
        public event EventHandler DraftPreviewChanged;
        private void NotifyDraftPreview(object sender, EventArgs args) => DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
        void ICabinetDraftWorkflow.ReleaseDraftWindow()
        {
            if (_cabinetDraftDocument == null) return;
            _cabinetDraftDocument.Session.Changed -= NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready -= NotifyDraftPreview;
        }
        CabinetDrawingNumberMetadataSession ICabinetDraftWorkflow.DrawingNumberSession => _drawingNumberMetadataSession;
        void ICabinetDraftWorkflow.CarryDraftProject(string projectPath, string drawingMetadata)
        {
            ProjectPath = projectPath;
            if (!string.IsNullOrWhiteSpace(drawingMetadata) && !_drawingNumberMetadataSession.TryCommit(drawingMetadata, out var error))
                throw new InvalidOperationException(error);
        }
        void ICabinetDraftWorkflow.RestoreDraftInputs(BridgeParameterDraftDocument draft)
        {
            var restored = draft.Session.Parameters().ToObject<SideToSideCabinetDriverParameters>();
            ProjectPath = restored.ProjectPath;
            Parameters = restored;
            if (!string.IsNullOrWhiteSpace(draft.Session.DrawingMetadata) &&
                !_drawingNumberMetadataSession.TryCommit(draft.Session.DrawingMetadata, out var error))
                throw new InvalidOperationException(error);
            RaisePropertyChanged(nameof(Parameters));
        }
        void ICabinetDraftWorkflow.PreviewCabinetInputs(BridgeParameterDraftDocument expectedDraft, bool resetScenario)
            => StartParameterPreview(false, expectedDraft, resetScenario);
        BridgeParameterDraftDocument ICabinetDraftWorkflow.SubmitCabinetInputs()
        {
            if (Parameters == null) throw new InvalidOperationException("柜体参数尚未就绪。");
            if (!SideToSideCabinetGenerationPlan.TryValidateConfirmedDriveScope(Parameters, out var error, validateNominalOpeningElevation: false))
            {
                BaseParametersRequested?.Invoke(error);
                throw new InvalidOperationException(error);
            }
            return StartParameterPreview();
        }
        private BridgeParameterDraftDocument StartParameterPreview(bool confirmDrawingMetadata = true, BridgeParameterDraftDocument expectedDraft = null, bool resetScenario = false)
        {
            if (_swApp == null) throw new InvalidOperationException("未连接到 SolidWorks，无法建立柜体草稿。");
            if (confirmDrawingMetadata)
            {
                if (!_drawingNumberMetadataSession.TryEnsureCommitted(out var error)) throw new InvalidOperationException(error);
                CKBusbarBridge.Prototype.CabinetParameterPropertyCodec.DrawingInputs(_drawingNumberMetadataSession.CommittedMetadataJson);
            }
            var draft = BridgeParameterDraftDocuments.For(_swApp).Begin(Parameters, "side-to-side", null,
                _drawingNumberMetadataSession.CommittedMetadataJson, expectedDraft ?? _cabinetDraftDocument, resetScenario, confirmDocument: confirmDrawingMetadata);
            ((ICabinetDraftWorkflow)this).ReleaseDraftWindow();
            _cabinetDraftDocument = draft;
            _cabinetDraftDocument.Session.Changed += NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready += NotifyDraftPreview;
            DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
            return draft;
        }
    }
    public partial class FaceToWallCabinetDriverWindowVM : ICabinetDraftWorkflow
    {
        private BridgeParameterDraftDocument _cabinetDraftDocument;
        BridgeParameterDraftDocument ICabinetDraftWorkflow.DraftDocument => _cabinetDraftDocument;
        public event EventHandler DraftPreviewChanged;
        private void NotifyDraftPreview(object sender, EventArgs args) => DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
        void ICabinetDraftWorkflow.ReleaseDraftWindow()
        {
            if (_cabinetDraftDocument == null) return;
            _cabinetDraftDocument.Session.Changed -= NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready -= NotifyDraftPreview;
        }
        CabinetDrawingNumberMetadataSession ICabinetDraftWorkflow.DrawingNumberSession => _drawingNumberMetadataSession;
        void ICabinetDraftWorkflow.CarryDraftProject(string projectPath, string drawingMetadata)
        {
            ProjectPath = projectPath;
            if (!string.IsNullOrWhiteSpace(drawingMetadata) && !_drawingNumberMetadataSession.TryCommit(drawingMetadata, out var error))
                throw new InvalidOperationException(error);
        }
        void ICabinetDraftWorkflow.RestoreDraftInputs(BridgeParameterDraftDocument draft)
        {
            var restored = draft.Session.Parameters().ToObject<FaceToWallCabinetDriverParameters>();
            ProjectPath = restored.ProjectPath;
            Parameters = restored;
            if (!string.IsNullOrWhiteSpace(draft.Session.DrawingMetadata) &&
                !_drawingNumberMetadataSession.TryCommit(draft.Session.DrawingMetadata, out var error))
                throw new InvalidOperationException(error);
            RaisePropertyChanged(nameof(Parameters));
        }
        void ICabinetDraftWorkflow.PreviewCabinetInputs(BridgeParameterDraftDocument expectedDraft, bool resetScenario)
            => StartParameterPreview(false, expectedDraft, resetScenario);
        BridgeParameterDraftDocument ICabinetDraftWorkflow.SubmitCabinetInputs()
        {
            if (Parameters == null) throw new InvalidOperationException("柜体参数尚未就绪。");
            if (!TryValidateStandaloneBasePlans(Parameters, out var error))
                throw new InvalidOperationException(error);
            return StartParameterPreview();
        }
        private BridgeParameterDraftDocument StartParameterPreview(bool confirmDrawingMetadata = true, BridgeParameterDraftDocument expectedDraft = null, bool resetScenario = false)
        {
            if (_swApp == null) throw new InvalidOperationException("未连接到 SolidWorks，无法建立柜体草稿。");
            if (confirmDrawingMetadata)
            {
                if (!_drawingNumberMetadataSession.TryEnsureCommitted(out var error)) throw new InvalidOperationException(error);
                CKBusbarBridge.Prototype.CabinetParameterPropertyCodec.DrawingInputs(_drawingNumberMetadataSession.CommittedMetadataJson);
            }
            var draft = BridgeParameterDraftDocuments.For(_swApp).Begin(Parameters, "face-to-wall", CabinetOrientation.ToString(),
                _drawingNumberMetadataSession.CommittedMetadataJson, expectedDraft ?? _cabinetDraftDocument, resetScenario, confirmDocument: confirmDrawingMetadata);
            ((ICabinetDraftWorkflow)this).ReleaseDraftWindow();
            _cabinetDraftDocument = draft;
            _cabinetDraftDocument.Session.Changed += NotifyDraftPreview;
            _cabinetDraftDocument.Session.Ready += NotifyDraftPreview;
            DraftPreviewChanged?.Invoke(this, EventArgs.Empty);
            return draft;
        }
    }
}
