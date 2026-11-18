using System;
using System.Collections.Generic;
using System.Threading;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Routing
{
    public interface ILinkageMultiGroupRouteSolver
    {
        LinkageRouteAlgorithmKind AlgorithmKind { get; }

        IReadOnlyList<LinkageRouteSolution> Solve(
            LinkageMultiGroupRouteRequest routeRequest,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken);
    }
}
