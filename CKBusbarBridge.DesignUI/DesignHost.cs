using System;
using System.Collections.Generic;
using CKBusbarBridge.Services;
using CKBusbarBridge.AlgorithmWorker;
namespace CKBusbarBridge.Prototype
{
    internal interface IBridgeDesignHost
    {
        IntPtr OwnerHandle { get; }
        string ResolveWorkerAuthorization();
        IBridgeUpstreamReader UpstreamReader { get; }
        IBridgeDesignPreviewSession CreatePreviewSession();
        BridgeDesignSample RestoreSavedDraft(BridgeUpstreamSnapshot upstream, BridgeDesignSample fresh);
        BridgeDesignSample ReadHangerContext(BridgeUpstreamSnapshot upstream, BridgeDesignSample sample);
        BridgeDesignSample PlanRepair(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual);
        BridgeWallPlanningRestore DecodeWallPlanning(string recipe);
        void ValidateWallSpanGeneration(BridgeUpstreamSnapshot upstream, BridgeDesignSample candidate);
    }
    internal interface IBridgeDesignPreviewSession : IDisposable
    {
        bool Enabled { get; }
        bool IsBound { get; }
        event EventHandler Invalidated;
        void Bind(BridgeUpstreamSnapshot upstream);
        void Clear();
        void Refresh(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision, Func<bool> isCurrent, bool activate);
    }
    internal sealed class BridgeDesignGenerationResult
    {
        internal BridgeDesignSample Readback { get; set; }
        internal BridgeUpstreamSnapshot Upstream { get; set; }
        internal string Message { get; set; }
        internal bool BlocksGeneration { get; set; }
    }
    internal sealed class BridgeWallPlanningRestore
    {
        internal bool IsPhaseSwitched { get; set; }
        internal double? MinimumSpecialSpan { get; set; }
        internal double? SpecialStartX { get; set; }
        internal LinkageRouteWorkerRequestDto Request { get; set; }
        internal LinkageRouteWorkerSolutionDto Solution { get; set; }
        internal AlgorithmBusbarGenerationGroup[] Groups { get; set; }
    }
}
