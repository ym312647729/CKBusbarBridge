using System;
using System.Threading;

namespace CKBusbarBridge.Computation
{
    public static class CalculationConcurrency
    {
        private static int _activeRoutingSearches;
        public static int MaximumWorkers => Math.Max(1, System.Environment.ProcessorCount - 3);
        public static int RoutingWorkers => Math.Max(1, MaximumWorkers - 1);
        public static int PreviewWorkers => Volatile.Read(ref _activeRoutingSearches) > 0 ? 1 : MaximumWorkers;

        public static IDisposable BeginRouting()
        {
            Interlocked.Increment(ref _activeRoutingSearches);
            return new RoutingReservation();
        }

        private sealed class RoutingReservation : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    Interlocked.Decrement(ref _activeRoutingSearches);
            }
        }
    }
}
