using System;
using System.Collections.Generic;
using System.Threading;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    public sealed class SweptRepairLinkageRouteSolver : ILinkageMultiGroupRouteSolver
    {
        public LinkageRouteAlgorithmKind AlgorithmKind => LinkageRouteAlgorithmKind.SweptRepair;

        public IReadOnlyList<LinkageRouteSolution> Solve(
            LinkageMultiGroupRouteRequest routeRequest,
            IProgress<LinkageRouteProgress> progress,
            CancellationToken cancellationToken)
        {
            return LinkageMultiGroupRouteEngine.Solve(
                routeRequest, AlgorithmKind, progress, cancellationToken);
        }
    }
}
