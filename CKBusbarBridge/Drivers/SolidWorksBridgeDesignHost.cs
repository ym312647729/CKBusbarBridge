using System;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
namespace CKBusbarBridge.Drivers
{
    internal sealed class SolidWorksBridgeDesignHost : IBridgeDesignHost, IBridgeParameterDraftHost
    {
        private readonly ISldWorks _app;
        private BridgeParameterDraftDocument _draft;
        private BusbarBridgeDesignWindowVM _draftEditor;
        private Action _discardDraftSettings;
        public bool HasParameterDraft => (_draft ?? BridgeParameterDraftDocuments.For(_app).Active) != null;
        public event EventHandler ParameterDraftChanged;
        public System.Threading.Tasks.Task EnsureParameterDraftReady(bool recommend = false)
        {
            var draft = BridgeParameterDraftDocuments.For(_app).Active;
            return draft == null ? System.Threading.Tasks.Task.CompletedTask : draft.EnsureReady(recommend);
        }
        private BridgeParameterDraftDocument ActiveDraft()
        {
            if (_draft != null && !ReferenceEquals(_app.IActiveDoc2, _draft.Document))
                throw new InvalidOperationException("请先激活此参数面板对应的总装，再继续操作。");
            return _draft ?? BridgeParameterDraftDocuments.For(_app).Active;
        }
        public void ShowDraftView(BridgeDraftView view) => ActiveDraft()?.ShowView(view);
        public System.Threading.Tasks.Task PreviewDraftScheme(BridgeDesignSample sample)
            => (ActiveDraft() ?? throw new InvalidOperationException("参数草稿已关闭。")).PreviewScheme(sample);
        public void RestoreDraftPreview() => _draft?.RestorePreview();
        public System.Threading.Tasks.Task ConfirmDraftSettings(bool requireReady = false)
            => (ActiveDraft() ?? throw new InvalidOperationException("参数草稿已关闭。")).ConfirmCabinetInputs(requireReady);
        public void DiscardDraftSettings() { _discardDraftSettings?.Invoke(); _discardDraftSettings = null; }
        public IBridgeUpstreamReader UpstreamReader { get; }
        public IntPtr OwnerHandle => (_app.Frame() as IFrame) is IFrame frame ? new IntPtr(frame.GetHWndx64()) : IntPtr.Zero;
        internal SolidWorksBridgeDesignHost(ISldWorks app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            UpstreamReader = new ParameterAwareReader(this);
        }
        public string ResolveWorkerAuthorization() => CKBusbarBridge.Services.BusbarWorkerAuthorization.Resolve();
        public void ValidateWallSpanGeneration(BridgeUpstreamSnapshot upstream, BridgeDesignSample candidate)
        {
            var editor = new BusbarBridgeDraftEditor(); editor.Load(candidate);
            new BridgeDraftGenerationPlanFactory().Create(upstream, editor);
        }
        public IBridgeDesignPreviewSession CreatePreviewSession()
        {
            var draft = BridgeParameterDraftDocuments.For(_app).Active;
            return new PreviewLease(draft ?? throw new InvalidOperationException("请先打开本项目参数草稿。"));
        }
        public BridgeDesignSample RestoreSavedDraft(BridgeUpstreamSnapshot upstream, BridgeDesignSample fresh)
            => ActiveDraft()?.RestoreRecipe(upstream);
        public BridgeDesignSample ReadHangerContext(BridgeUpstreamSnapshot upstream, BridgeDesignSample sample)
            => ActiveDraft()?.ReadHangerContext(sample);
        public BridgeDesignSample PlanRepair(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual)
            => new BridgeCabinetRepairPlanner().Create(upstream, actual, null);
        public BridgeWallPlanningRestore DecodeWallPlanning(string recipe)
        {
            var stored = new BridgeGenerationRecipeCodec().DecodeWallPlanning(recipe);
            return new BridgeWallPlanningRestore { Request = stored.Request, Solution = stored.Solution, Groups = stored.Groups,
                SpecialStartX = stored.SpecialStartX, IsPhaseSwitched = stored.IsPhaseSwitched,
                MinimumSpecialSpan = stored.MinimumSpecialSpan };
        }
        public void AttachDraftEditor(BusbarBridgeDesignWindowVM viewModel)
        {
            _draft = BridgeParameterDraftDocuments.For(_app).Active;
            if (_draft == null) return;
            _discardDraftSettings = _draft.CaptureSettings();
            _draft.Attach(viewModel); _draft.ParametersChanged += DraftChanged; _draftEditor = viewModel;
            ((DSldWorksEvents_Event)_app).ActiveDocChangeNotify += ActiveDraftDocumentChanged;
            ActiveDraftDocumentChanged();
        }
        public void DetachDraftEditor(BusbarBridgeDesignWindowVM viewModel)
        {
            if (_draft == null) return;
            ((DSldWorksEvents_Event)_app).ActiveDocChangeNotify -= ActiveDraftDocumentChanged;
            _draft.Detach(viewModel); _draft.ParametersChanged -= DraftChanged; _draft = null; _draftEditor = null;
            _discardDraftSettings = null;
        }
        private int ActiveDraftDocumentChanged()
        {
            try
            {
                if (_draftEditor != null) _draftEditor.IsDraftDocumentActive = _draft != null && ReferenceEquals(_app.IActiveDoc2, _draft.Document);
            }
            catch (Exception error)
            {
                if (_draftEditor != null) _draftEditor.IsDraftDocumentActive = false;
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("刷新参数面板的活动总装状态失败", error);
            }
            return 0;
        }
        private void DraftChanged(object sender, EventArgs args)
        {
            ActiveDraftDocumentChanged();
            ParameterDraftChanged?.Invoke(this, EventArgs.Empty);
        }
        private sealed class ParameterAwareReader : IBridgeUpstreamReader
        {
            private readonly SolidWorksBridgeDesignHost _owner;
            internal ParameterAwareReader(SolidWorksBridgeDesignHost owner) { _owner = owner; }
            public BridgeUpstreamSnapshot Read()
            {
                var draft = BridgeParameterDraftDocuments.For(_owner._app).Active;
                if (draft == null) throw new InvalidOperationException("请先打开本项目参数草稿。");
                if (!draft.Session.IsReady) throw new InvalidOperationException(draft.Session.Failure ?? "参数草稿正在计算，请稍候。");
                return draft.Session.Upstream;
            }
        }
        private sealed class PreviewLease : IBridgeDesignPreviewSession
        {
            private readonly BridgeParameterDraftDocument _document;
            internal PreviewLease(BridgeParameterDraftDocument document) { _document = document; }
            public bool Enabled => _document.Enabled;
            public bool IsBound => _document.IsBound;
            public event EventHandler Invalidated { add { _document.Invalidated += value; } remove { _document.Invalidated -= value; } }
            public void Bind(BridgeUpstreamSnapshot upstream) => _document.Bind(upstream);
            public void Clear() => _document.ClearBridgeSelection();
            public void Refresh(BridgeDesignSample sample, System.Collections.Generic.IReadOnlyList<BridgeDesignObject> objects, long revision, Func<bool> isCurrent, bool activate)
                => _document.Refresh(sample, objects, revision, isCurrent, activate);
            public void Dispose() { }
        }
    }
}
