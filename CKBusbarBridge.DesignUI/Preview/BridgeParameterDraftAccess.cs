using System;
using System.Runtime.CompilerServices;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Prototype
{
    internal enum BridgeDraftView { All, Selected, Top, Front, Isometric }
    internal interface IBridgeParameterDraftLookup
    {
        BridgeDesignSession Find(IModelDoc2 model);
    }
    internal static class BridgeParameterDraftAccess
    {
        private static readonly ConditionalWeakTable<ISldWorks, IBridgeParameterDraftLookup> Owners = new ConditionalWeakTable<ISldWorks, IBridgeParameterDraftLookup>();
        internal static void Register(ISldWorks application, IBridgeParameterDraftLookup owner)
        { Owners.Remove(application); Owners.Add(application, owner); }
        internal static void Unregister(ISldWorks application) { Owners.Remove(application); }
        internal static BridgeDesignSession Find(ISldWorks application, IModelDoc2 document)
            => application != null && document != null && Owners.TryGetValue(application, out var owner) ? owner.Find(document) : null;
    }
    internal interface IBridgeParameterDraftHost
    {
        bool HasParameterDraft { get; }
        System.Threading.Tasks.Task EnsureParameterDraftReady(bool recommend = false);
        void ShowDraftView(BridgeDraftView view);
        System.Threading.Tasks.Task PreviewDraftScheme(BridgeDesignSample sample);
        void RestoreDraftPreview();
        event EventHandler ParameterDraftChanged;
        void AttachDraftEditor(BusbarBridgeDesignWindowVM viewModel);
        void DetachDraftEditor(BusbarBridgeDesignWindowVM viewModel);
        System.Threading.Tasks.Task ConfirmDraftSettings(bool requireReady = false);
        void DiscardDraftSettings();
    }
}
