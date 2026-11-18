using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RouteLinkBar.Algorithm.Routing
{
    // A permit resumes an existing build stack at a completed candidate boundary.
    // Only the owner reads the mutable builder pool; consumers see snapshots.
    internal sealed class LinkageRoutePoolContinuation : IDisposable
    {
        private sealed class GroupContinuation
        {
            internal readonly SemaphoreSlim Resume = new SemaphoreSlim(0, 1);
            internal Task BuildTask;
            internal LinkageRouteCandidatePool Snapshot;
            internal bool Running;
            internal bool Completed;
            internal ExceptionDispatchInfo BuildFailure;
            internal long EvaluatedSkeletonCount;
            internal readonly Dictionary<LinkageRouteCandidatePool, long> EvaluationCounts =
                new Dictionary<LinkageRouteCandidatePool, long>();
        }

        private readonly object continuationLock = new object();
        private readonly GroupContinuation[] groupContinuations;
        private readonly Func<int, Action<LinkageRouteCandidatePool>, CancellationToken,
            LinkageRouteCandidatePool> buildPool;
        private readonly CancellationTokenSource continuationCancellation;
        private readonly AutoResetEvent progressAvailable = new AutoResetEvent(false);
        private readonly Action<CancellationToken> enterComputation;
        private readonly Action leaveComputation;

        public LinkageRoutePoolContinuation(int groupCount,
            Func<int, Action<LinkageRouteCandidatePool>, CancellationToken, LinkageRouteCandidatePool> builder,
            CancellationToken cancellationToken)
            : this(groupCount, builder, cancellationToken, null, null) { }

        public LinkageRoutePoolContinuation(int groupCount,
            Func<int, Action<LinkageRouteCandidatePool>, CancellationToken, LinkageRouteCandidatePool> builder,
            CancellationToken cancellationToken, Action<CancellationToken> enterWorker, Action leaveWorker)
        {
            buildPool = builder ?? throw new ArgumentNullException(nameof(builder));
            groupContinuations = Enumerable.Range(0, groupCount).Select(_ => new GroupContinuation()).ToArray();
            continuationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            enterComputation = enterWorker;
            leaveComputation = leaveWorker;
        }

        public bool HasPending
        {
            get { lock (continuationLock) return groupContinuations.Any(group => !group.Completed); }
        }

        public bool HasRunning
        {
            get { lock (continuationLock) return groupContinuations.Any(group => group.Running); }
        }

        public void Advance(IReadOnlyList<int> priorityIndices, int maximumConcurrency)
        {
            continuationCancellation.Token.ThrowIfCancellationRequested();
            lock (continuationLock)
            {
                ThrowBuildFailure();
                var availableWorkers = Math.Max(1, maximumConcurrency) - groupContinuations.Count(group => group.Running);
                foreach (var groupIndex in priorityIndices.Distinct())
                {
                    if (availableWorkers <= 0) break;
                    var continuation = groupContinuations[groupIndex];
                    if (continuation.Completed || continuation.Running) continue;
                    continuation.Running = true;
                    if (continuation.BuildTask == null)
                    {
                        // Waiting builders must not exhaust the shared thread pool
                        // used by combination search and cancellation callbacks.
                        continuation.BuildTask = Task.Factory.StartNew(() => BuildGroup(groupIndex),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    continuation.Resume.Release();
                    availableWorkers--;
                }
            }
        }

        public void WaitForProgress()
        {
            WaitHandle.WaitAny(new[] { progressAvailable, continuationCancellation.Token.WaitHandle }, 1000);
            continuationCancellation.Token.ThrowIfCancellationRequested();
            lock (continuationLock) ThrowBuildFailure();
        }

        public LinkageRouteCandidatePool[] CapturePools()
        {
            lock (continuationLock) return groupContinuations.Select(group => group.Snapshot).ToArray();
        }

        private void BuildGroup(int groupIndex)
        {
            var continuation = groupContinuations[groupIndex];
            var token = continuationCancellation.Token;
            var computationHeld = false;
            Action resumeBuild = () =>
            {
                continuation.Resume.Wait(token);
                enterComputation?.Invoke(token);
                computationHeld = true;
            };
            Action releaseBuild = () =>
            {
                if (!computationHeld) return;
                computationHeld = false;
                leaveComputation?.Invoke();
            };
            try
            {
                resumeBuild();
                var completedPool = buildPool(groupIndex, pool =>
                {
                    token.ThrowIfCancellationRequested();
                    releaseBuild();
                    lock (continuationLock)
                    {
                        CaptureSnapshot(continuation, pool);
                        continuation.Running = false;
                    }
                    progressAvailable.Set();
                    resumeBuild();
                }, token);
                lock (continuationLock) CaptureSnapshot(continuation, completedPool);
            }
            catch (OperationCanceledException stopped) when (token.IsCancellationRequested &&
                stopped.CancellationToken == token) { }
            catch (Exception failure)
            {
                lock (continuationLock) continuation.BuildFailure = ExceptionDispatchInfo.Capture(failure);
            }
            finally
            {
                releaseBuild();
                lock (continuationLock)
                {
                    continuation.Running = false;
                    continuation.Completed = true;
                    continuation.EvaluationCounts.Clear();
                }
                progressAvailable.Set();
            }
        }

        private void ThrowBuildFailure()
        {
            foreach (var continuation in groupContinuations)
            {
                continuation.BuildFailure?.Throw();
                if (continuation.BuildTask?.IsFaulted == true)
                    continuation.BuildTask.GetAwaiter().GetResult();
            }
        }

        private static void CaptureSnapshot(GroupContinuation continuation, LinkageRouteCandidatePool source)
        {
            if (source == null) return;
            continuation.EvaluationCounts.TryGetValue(source, out var previousCount);
            continuation.EvaluatedSkeletonCount += Math.Max(0, source.EvaluatedSkeletonCount - previousCount);
            continuation.EvaluationCounts[source] = source.EvaluatedSkeletonCount;
            continuation.Snapshot = Snapshot(source, continuation.Snapshot, continuation.EvaluatedSkeletonCount);
        }

        private static LinkageRouteCandidatePool Snapshot(LinkageRouteCandidatePool source,
            LinkageRouteCandidatePool previous, long evaluatedSkeletonCount)
        {
            if (source == null) return null;
            var snapshot = new LinkageRouteCandidatePool
            {
                GroupRequest = source.GroupRequest,
                BestFailedCandidate = source.BestFailedCandidate,
                EvaluatedSkeletonCount = evaluatedSkeletonCount
            };
            foreach (var direction in source.TraversalDirections) snapshot.TraversalDirections.Add(direction);
            var completedCandidates = new HashSet<LinkageRouteCandidate>();
            foreach (var candidate in (previous?.ValidCandidates ?? Array.Empty<LinkageRouteCandidate>())
                .Concat(source.ValidCandidates))
                if (completedCandidates.Add(candidate)) snapshot.ValidCandidates.Add(candidate);
            foreach (var diagnostic in source.Diagnostics) snapshot.Diagnostics.Add(diagnostic);
            foreach (var pair in source.FailureStageCounts) snapshot.FailureStageCounts.Add(pair);
            foreach (var pair in source.RepresentativeFailedCandidates) snapshot.RepresentativeFailedCandidates.Add(pair);
            foreach (var pair in source.FailureDiagnosticSamples)
                snapshot.FailureDiagnosticSamples.Add(pair.Key, pair.Value.ToList());
            return snapshot;
        }

        public void Dispose()
        {
            continuationCancellation.Cancel();
            var buildTasks = groupContinuations.Select(group => group.BuildTask).Where(task => task != null).ToArray();
            try
            {
                Task.WaitAll(buildTasks);
                lock (continuationLock) ThrowBuildFailure();
            }
            finally
            {
                foreach (var group in groupContinuations) group.Resume.Dispose();
                progressAvailable.Dispose();
                continuationCancellation.Dispose();
            }
        }
    }
}
