using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface ISideToSideBaseSecondaryOperations
    {
        SideToSideBaseSecondarySnapshot CaptureCurrentSnapshot(
            SideToSideCabinetParameterSnapshot parameters);

        void ApplyPlan(
            string projectPath,
            SideToSideBaseSecondaryDrivePlan plan,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            IDrawingNumberGenerator drawingNumbers,
            IBaseModelLifecycleTransaction transaction);
    }

    internal sealed class SideToSideBaseSecondaryDriveResult
    {
        internal SideToSideBaseSecondaryDriveResult(
            bool hasChanges,
            SideToSideBaseSecondarySnapshot committedSnapshot,
            IReadOnlyList<BaseInstallationChange> installationChanges = null)
        {
            HasChanges = hasChanges;
            CommittedSnapshot = committedSnapshot
                ?? throw new ArgumentNullException(nameof(committedSnapshot));
            InstallationChanges = installationChanges ?? Array.Empty<BaseInstallationChange>();
        }

        internal bool HasChanges { get; }
        internal SideToSideBaseSecondarySnapshot CommittedSnapshot { get; }
        internal IReadOnlyList<BaseInstallationChange> InstallationChanges { get; }
    }

    internal sealed class SideToSideBaseSecondaryDriveService
    {
        private readonly ISideToSideBaseSecondaryOperations _operations;
        private readonly Func<IBaseModelLifecycleTransaction> _transactionFactory;

        internal SideToSideBaseSecondaryDriveService(
            ISideToSideBaseSecondaryOperations operations,
            Func<IBaseModelLifecycleTransaction> transactionFactory)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _transactionFactory = transactionFactory
                ?? throw new ArgumentNullException(nameof(transactionFactory));
        }

        internal SideToSideBaseSecondaryDriveResult Update(
            string projectPath,
            SideToSideBaseSecondarySnapshot snapshot,
            SideToSideCabinetDriverParameters currentParameters,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目目录不能为空。", nameof(projectPath));
            }
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (currentParameters == null)
            {
                throw new ArgumentNullException(nameof(currentParameters));
            }

            var liveSnapshot = _operations.CaptureCurrentSnapshot(snapshot.Parameters);
            SideToSideBaseSecondarySnapshotComparer.EnsureSamePhysicalState(
                snapshot,
                liveSnapshot);
            var targetPositions = SideToSideBaseDrivePlanner.CreatePlans(currentParameters);
            var plan = SideToSideBaseSecondaryDrivePlanner.Create(
                liveSnapshot,
                targetPositions,
                projectPath);
            if (plan.IsEmpty)
            {
                return new SideToSideBaseSecondaryDriveResult(false, liveSnapshot);
            }

            var transaction = _transactionFactory();
            if (transaction == null)
            {
                throw new InvalidOperationException("无法创建侧对侧基座生命周期事务。");
            }

            transaction.SaveBaseline();
            try
            {
                _operations.ApplyPlan(
                    projectPath,
                    plan,
                    targetPositions,
                    drawingNumbers,
                    transaction);
                var committed = _operations.CaptureCurrentSnapshot(
                    SideToSideCabinetParameterSnapshot.Capture(currentParameters));
                var installationChanges = BaseInstallationChangeFactory.Create(liveSnapshot, committed);
                transaction.Complete();
                return new SideToSideBaseSecondaryDriveResult(true, committed, installationChanges);
            }
            catch
            {
                transaction.Restore();
                throw;
            }
        }
    }

    /// <summary>沿用面对面首基架锚点契约，仅管理本次侧对侧基座替换的连接记录。</summary>
    internal sealed class SideToSideBaseFirstFrameAnchors
    {
        private readonly Func<string, string, FaceToFaceBaseFirstFrameAnchor> _detach;
        private readonly Action<SideToSideBaseAssemblyToken, FaceToFaceBaseFirstFrameAnchor> _restore;
        private readonly Dictionary<string, FaceToFaceBaseFirstFrameAnchor> _anchors =
            new Dictionary<string, FaceToFaceBaseFirstFrameAnchor>(StringComparer.Ordinal);

        internal SideToSideBaseFirstFrameAnchors(
            Func<string, string, FaceToFaceBaseFirstFrameAnchor> detach,
            Action<SideToSideBaseAssemblyToken, FaceToFaceBaseFirstFrameAnchor> restore)
        {
            _detach = detach ?? throw new ArgumentNullException(nameof(detach));
            _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        }

        internal void CaptureBeforeRemove(string positionName, SideToSideBaseModelKind kind, string componentFullName)
        {
            if (kind == SideToSideBaseModelKind.ElevatedBase) return;
            if (_anchors.ContainsKey(positionName))
                throw new InvalidOperationException("重复拆除已记录首基架连接的基座位置：" + positionName);
            var anchor = _detach(positionName, componentFullName);
            if (anchor == null) return;
            if (!string.Equals(positionName, anchor.PositionName, StringComparison.Ordinal))
                throw new InvalidOperationException("首基架连接记录与基座位置不一致：" + positionName);
            _anchors.Add(positionName, anchor);
        }

        internal void RestoreBeforeSave(IReadOnlyList<SideToSideBaseAssemblyToken> tokens)
        {
            foreach (var entry in _anchors)
            {
                var targets = tokens.Where(token => token.Kind != SideToSideBaseModelKind.ElevatedBase
                    && string.Equals(token.PositionName, entry.Key, StringComparison.Ordinal)).ToArray();
                if (targets.Length != 1)
                    throw new InvalidOperationException("无法唯一定位恢复首基架连接的新普通基座：" + entry.Key);
                _restore(targets[0], entry.Value);
            }
        }
    }

    internal static class SideToSideBaseSecondarySnapshotComparer
    {
        internal static void EnsureSamePhysicalState(
            SideToSideBaseSecondarySnapshot expected,
            SideToSideBaseSecondarySnapshot actual)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }
            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }
            if (expected.Models.Count != actual.Models.Count)
            {
                throw new InvalidOperationException(
                    "侧对侧基座物理引用数量已变化，请重新打开界面后再驱动。");
            }

            foreach (var oldModel in expected.Models)
            {
                var current = actual.Models.SingleOrDefault(model =>
                    model.Kind == oldModel.Kind
                    && string.Equals(
                        model.PositionName,
                        oldModel.PositionName,
                        StringComparison.Ordinal));
                if (current == null
                    || !SameText(oldModel.ComponentFullName, current.ComponentFullName)
                    || !SamePath(oldModel.ModelPath, current.ModelPath)
                    || !oldModel.Identity.Equals(current.Identity)
                    || !SameNameState(oldModel.NameState, current.NameState)
                    || !SamePaths(oldModel.InsulatorPaths, current.InsulatorPaths))
                {
                    throw new InvalidOperationException(
                        oldModel.PositionName
                        + "/"
                        + oldModel.Kind
                        + "的基座物理引用已变化，请重新打开界面后再驱动。");
                }
            }
        }

        private static bool SameNameState(
            BaseModelNameState left,
            BaseModelNameState right)
        {
            return SameText(left.TemplateAssemblyBaseName, right.TemplateAssemblyBaseName)
                && SameText(left.CurrentAssemblyBaseName, right.CurrentAssemblyBaseName)
                && SameText(
                    left.CurrentDrawingNumberSuffix,
                    right.CurrentDrawingNumberSuffix);
        }

        private static bool SamePaths(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            return left.Count == right.Count
                && left.All(path => right.Any(candidate => SamePath(path, candidate)));
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
