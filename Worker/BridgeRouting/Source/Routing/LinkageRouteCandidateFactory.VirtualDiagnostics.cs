using System;
namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        // Isolated virtual diagnostic hook; no scheduling, geometry or return-value changes.
        internal static Action<object, string> VirtualClearanceTraversalObserver;
    }
}
