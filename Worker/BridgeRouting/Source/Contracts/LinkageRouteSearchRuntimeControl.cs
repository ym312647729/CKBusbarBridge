using System;
using System.Threading;

namespace RouteLinkBar.Algorithm.Contracts
{
    /// <summary>
    /// 单次求解会话的运行时调度控制。它不属于业务 Json，也不改变输入契约；
    /// 只用于宿主在 Worker 求解期间停止新方案搜索并切换到已有方案优化。
    /// </summary>
    public sealed class LinkageRouteSearchRuntimeControl : IDisposable
    {
        private readonly CancellationTokenSource newSolutionSearchCancellationSource =
            new CancellationTokenSource();
        private int optimizationPriorityRequested;
        private int disposed;

        internal event Action OptimizationPriorityRequested;

        public bool IsOptimizationPriorityRequested =>
            Volatile.Read(ref optimizationPriorityRequested) != 0;

        public CancellationToken NewSolutionSearchCancellationToken =>
            newSolutionSearchCancellationSource.Token;

        public bool RequestOptimizationPriority()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.CompareExchange(
                    ref optimizationPriorityRequested,
                    1,
                    0) != 0)
            {
                return false;
            }

            try
            {
                newSolutionSearchCancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            var priorityRequestedHandlers = OptimizationPriorityRequested;
            priorityRequestedHandlers?.Invoke();
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            OptimizationPriorityRequested = null;
            newSolutionSearchCancellationSource.Dispose();
        }
    }
}
