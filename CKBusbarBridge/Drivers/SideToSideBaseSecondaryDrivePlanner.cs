using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SideToSideExistingInsulatorDriveTarget
    {
        internal SideToSideExistingInsulatorDriveTarget(
            string modelPath,
            SideToSideInsulatorPlacementPlan placement)
        {
            ModelPath = string.IsNullOrWhiteSpace(modelPath)
                ? throw new ArgumentException("绝缘子模型路径不能为空。", nameof(modelPath))
                : System.IO.Path.GetFullPath(modelPath);
            Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        }

        internal string ModelPath { get; }
        internal SideToSideInsulatorPlacementPlan Placement { get; }
    }

    internal static class SideToSideExistingInsulatorDrivePlanner
    {
        internal static IReadOnlyList<SideToSideExistingInsulatorDriveTarget> Create(
            IReadOnlyList<SideToSideBasePhysicalModelSnapshot> physicalModels,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions)
        {
            if (physicalModels == null)
            {
                throw new ArgumentNullException(nameof(physicalModels));
            }
            if (targetPositions == null)
            {
                throw new ArgumentNullException(nameof(targetPositions));
            }

            var targets = new Dictionary<string, SideToSideExistingInsulatorDriveTarget>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var physical in physicalModels)
            {
                if (physical.InsulatorPaths.Count == 0)
                {
                    continue;
                }

                var position = targetPositions.SingleOrDefault(item =>
                    string.Equals(
                        item.PositionName,
                        physical.PositionName,
                        StringComparison.Ordinal));
                var placement = position?.Insulators.FirstOrDefault(item =>
                    item.HostModelKind == physical.Kind);
                if (placement == null)
                {
                    continue;
                }

                foreach (var path in physical.InsulatorPaths)
                {
                    if (targets.TryGetValue(path, out var existing))
                    {
                        EnsureSameDirectParameters(existing.Placement, placement, path);
                        continue;
                    }

                    targets.Add(
                        path,
                        new SideToSideExistingInsulatorDriveTarget(path, placement));
                }
            }

            return targets.Values.ToArray();
        }

        private static void EnsureSameDirectParameters(
            SideToSideInsulatorPlacementPlan left,
            SideToSideInsulatorPlacementPlan right,
            string modelPath)
        {
            if (!string.Equals(
                    left.BusbarSpec,
                    right.BusbarSpec,
                    StringComparison.OrdinalIgnoreCase)
                || !SideToSideCabinetChangeAnalyzer.SameNumber(
                    left.PhaseSpacing,
                    right.PhaseSpacing)
                || !SideToSideCabinetChangeAnalyzer.SameNumber(
                    left.InsulatorHeight,
                    right.InsulatorHeight)
                || !SideToSideCabinetChangeAnalyzer.SameNumber(
                    left.BracketHostSpan,
                    right.BracketHostSpan)
                || !SameLayout(left.Layout, right.Layout)
                || left.SinglePhaseRootCount != right.SinglePhaseRootCount)
            {
                throw new InvalidOperationException(
                    "同一绝缘子物理模型存在不同目标参数，必须先执行基座拆分："
                    + modelPath);
            }
        }

        private static bool SameLayout(UniversalInsulatorLayout left, UniversalInsulatorLayout right)
        {
            if (left == null || right == null) return left == right;
            return SideToSideCabinetChangeAnalyzer.SameNumber(left.BracketLength, right.BracketLength)
                && left.Distances.Zip(right.Distances, SideToSideCabinetChangeAnalyzer.SameNumber).All(same => same);
        }
    }

    internal static class SideToSideBaseModelOwnershipPolicy
    {
        internal static bool IsProjectOwned(
            string projectPath,
            string modelPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目目录不能为空。", nameof(projectPath));
            }
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("模型路径不能为空。", nameof(modelPath));
            }

            try
            {
                BaseModelTransactionPathGuard.EnsureSafeProjectModelDescendant(
                    projectPath,
                    System.IO.Path.GetDirectoryName(
                        System.IO.Path.GetFullPath(modelPath)));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal static IReadOnlyCollection<string> ResolveAllowedDetachedModelPaths(
            string projectPath,
            IReadOnlyList<SideToSideBasePositionApplication> applications,
            IEnumerable<string> pendingTemplatePaths)
        {
            if (applications == null)
            {
                throw new ArgumentNullException(nameof(applications));
            }
            if (pendingTemplatePaths == null)
            {
                throw new ArgumentNullException(nameof(pendingTemplatePaths));
            }

            var paths = new HashSet<string>(
                pendingTemplatePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(System.IO.Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            foreach (var model in applications
                .Where(application => application.RemoveBeforeTemplatePreparation)
                .SelectMany(application => application.Action.OldModels)
                .Where(model => !IsProjectOwned(projectPath, model.ModelPath)))
            {
                // 允许关闭总装快照中实际预拆的旧缓存模型；它们不会进入备份、驱动或退役链。
                paths.Add(System.IO.Path.GetFullPath(model.ModelPath));
            }

            return paths.ToArray();
        }
    }

    internal sealed class SideToSideBasePositionApplication
    {
        internal SideToSideBasePositionApplication(
            SideToSideBasePositionAction action,
            IEnumerable<string> templateTitleConflictModelPaths,
            bool removeBeforeTemplatePreparation)
        {
            Action = action ?? throw new ArgumentNullException(nameof(action));
            _removeBeforeTemplatePreparation = removeBeforeTemplatePreparation;
            TemplateTitleConflictModelPaths = Array.AsReadOnly((
                templateTitleConflictModelPaths
                ?? throw new ArgumentNullException(nameof(templateTitleConflictModelPaths)))
                .Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        internal SideToSideBasePositionAction Action { get; }

        internal IReadOnlyList<string> TemplateTitleConflictModelPaths { get; }

        private readonly bool _removeBeforeTemplatePreparation;

        internal bool RemoveBeforeTemplatePreparation =>
            _removeBeforeTemplatePreparation
            || TemplateTitleConflictModelPaths.Count > 0;

        internal bool RemoveDuringPositionApplication =>
            !RemoveBeforeTemplatePreparation
            && (Action.Kind == SideToSideBasePositionActionKind.Remove
                || Action.Kind == SideToSideBasePositionActionKind.Replace);

        internal bool InsertDuringPositionApplication =>
            Action.Kind == SideToSideBasePositionActionKind.Insert
            || Action.Kind == SideToSideBasePositionActionKind.Replace
            || (RemoveBeforeTemplatePreparation
                && Action.Kind == SideToSideBasePositionActionKind.Keep);

        internal bool PreserveOldMapping =>
            Action.Kind == SideToSideBasePositionActionKind.Keep
            && !RemoveBeforeTemplatePreparation;
    }

    internal static class SideToSideBasePositionApplicationPlanner
    {
        internal static IReadOnlyList<SideToSideBasePositionApplication> Create(
            IReadOnlyList<SideToSideBasePositionAction> actions,
            IEnumerable<string> pendingTemplateAssemblyPaths)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }
            if (pendingTemplateAssemblyPaths == null)
            {
                throw new ArgumentNullException(nameof(pendingTemplateAssemblyPaths));
            }

            var pendingTitles = new HashSet<string>(
                pendingTemplateAssemblyPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(System.IO.Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
            return actions.Select(action =>
            {
                var conflicts = action.OldModels
                    .Where(model => pendingTitles.Contains(
                        System.IO.Path.GetFileName(model.ModelPath)))
                    .Select(model => model.ModelPath);
                return new SideToSideBasePositionApplication(
                    action,
                    conflicts,
                    RequiresPreDetach(action));
            }).ToArray();
        }

        private static bool RequiresPreDetach(
            SideToSideBasePositionAction action)
        {
            if (action.Kind == SideToSideBasePositionActionKind.Replace
                || action.Kind == SideToSideBasePositionActionKind.Remove)
            {
                return true;
            }

            if (action.Kind != SideToSideBasePositionActionKind.Keep
                || action.TargetPlan == null)
            {
                return false;
            }

            var targetModels = action.TargetPlan.ElevatedBaseModel == null
                ? new[] { action.TargetPlan.BaseModel }
                : new[]
                {
                    action.TargetPlan.BaseModel,
                    action.TargetPlan.ElevatedBaseModel
                };
            return action.OldModels.Any(oldModel =>
            {
                var targetModel = targetModels.SingleOrDefault(
                    model => model.Kind == oldModel.Kind);
                return targetModel != null
                    && !oldModel.Identity.Equals(
                        SideToSideBaseLifecycleAdapter.CreateIdentity(
                            action.TargetPlan,
                            targetModel));
            });
        }
    }

    internal static class SideToSideBaseTemplateConflictLifecycle
    {
        internal static TResult PrepareAfterDetachingConflicts<TResult>(
            IReadOnlyList<SideToSideBasePositionApplication> applications,
            Action<IReadOnlyList<SideToSideBasePhysicalModelSnapshot>> removePosition,
            Action saveDetachedAssembly,
            Action<string> closeDetachedModel,
            Action driveDetachedExistingModels,
            Func<TResult> prepareTemplateModels)
        {
            if (applications == null)
            {
                throw new ArgumentNullException(nameof(applications));
            }
            if (removePosition == null)
            {
                throw new ArgumentNullException(nameof(removePosition));
            }
            if (saveDetachedAssembly == null)
            {
                throw new ArgumentNullException(nameof(saveDetachedAssembly));
            }
            if (closeDetachedModel == null)
            {
                throw new ArgumentNullException(nameof(closeDetachedModel));
            }
            if (driveDetachedExistingModels == null)
            {
                throw new ArgumentNullException(nameof(driveDetachedExistingModels));
            }
            if (prepareTemplateModels == null)
            {
                throw new ArgumentNullException(nameof(prepareTemplateModels));
            }

            var conflicts = applications
                .Where(application => application.RemoveBeforeTemplatePreparation)
                .ToArray();
            if (conflicts.Length > 0)
            {
                foreach (var conflict in conflicts)
                {
                    removePosition(conflict.Action.OldModels);
                }

                saveDetachedAssembly();
                foreach (var path in conflicts
                    .SelectMany(conflict => conflict.Action.OldModels)
                    .OrderBy(model => ModelDocumentClosePriority(model.Kind))
                    .Select(model => model.ModelPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    closeDetachedModel(path);
                }
            }

            driveDetachedExistingModels();
            return prepareTemplateModels();
        }

        private static int ModelDocumentClosePriority(
            SideToSideBaseModelKind kind)
        {
            // 基座2由增高基座宿主；先关闭宿主文档，才能确认子装配已卸载。
            return kind == SideToSideBaseModelKind.Base2 ? 1 : 0;
        }
    }

    internal static class SideToSideBaseSecondaryDrivePlanner
    {
        internal static SideToSideBaseSecondaryDrivePlan Create(
            SideToSideBaseSecondarySnapshot snapshot,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions)
        {
            return CreateCore(snapshot, targetPositions, snapshot?.Models);
        }

        internal static SideToSideBaseSecondaryDrivePlan Create(
            SideToSideBaseSecondarySnapshot snapshot,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            string projectPath)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var reusableModels = snapshot.Models
                .Where(model => SideToSideBaseModelOwnershipPolicy.IsProjectOwned(
                    projectPath,
                    model.ModelPath))
                .ToArray();
            return CreateCore(snapshot, targetPositions, reusableModels);
        }

        private static SideToSideBaseSecondaryDrivePlan CreateCore(
            SideToSideBaseSecondarySnapshot snapshot,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            IEnumerable<SideToSideBasePhysicalModelSnapshot> reusableModels)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (targetPositions == null)
            {
                throw new ArgumentNullException(nameof(targetPositions));
            }

            var targets = SideToSideBaseLifecycleAdapter.CreateTargets(targetPositions);
            var reuse = BaseModelReusePlanner.Create(
                reusableModels.Select(model => model.ToPhysicalModel()),
                targets,
                SideToSideBaseLifecycleAdapter.PositionOrder);
            var actions = SideToSideBaseLifecycleAdapter.PositionOrder
                .Select(positionName => CreatePositionAction(
                    positionName,
                    snapshot.Models,
                    targetPositions,
                    reuse))
                .ToArray();
            return new SideToSideBaseSecondaryDrivePlan(reuse, actions);
        }

        private static SideToSideBasePositionAction CreatePositionAction(
            string positionName,
            IReadOnlyList<SideToSideBasePhysicalModelSnapshot> oldModels,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            BaseModelReusePlan reuse)
        {
            var oldAtPosition = oldModels
                .Where(model => string.Equals(
                    model.PositionName,
                    positionName,
                    StringComparison.Ordinal))
                .ToArray();
            var target = targetPositions.SingleOrDefault(position =>
                string.Equals(
                    position.PositionName,
                    positionName,
                    StringComparison.Ordinal));
            var targetModels = GetTargetModels(target);
            var targetKeys = new Dictionary<SideToSideBaseModelKind, string>();
            foreach (var model in targetModels)
            {
                var action = reuse.ModelActions.Single(candidate =>
                    candidate.PositionNames.Contains(positionName)
                    && string.Equals(
                        candidate.RepresentativeTarget.ModelRoleKey,
                        model.Kind.ToString(),
                        StringComparison.Ordinal));
                targetKeys.Add(model.Kind, action.TargetKey);
            }

            SideToSideBasePositionActionKind kind;
            if (oldAtPosition.Length == 0 && target == null)
            {
                kind = SideToSideBasePositionActionKind.Keep;
            }
            else if (oldAtPosition.Length == 0)
            {
                kind = SideToSideBasePositionActionKind.Insert;
            }
            else if (target == null)
            {
                kind = SideToSideBasePositionActionKind.Remove;
            }
            else if (!SameKinds(oldAtPosition, targetModels)
                || TargetUsesDifferentPhysicalModels(
                    oldAtPosition,
                    targetModels,
                    reuse,
                    positionName))
            {
                kind = SideToSideBasePositionActionKind.Replace;
            }
            else
            {
                kind = SideToSideBasePositionActionKind.Keep;
            }

            return new SideToSideBasePositionAction(
                positionName,
                kind,
                target,
                oldAtPosition,
                targetKeys);
        }

        private static bool TargetUsesDifferentPhysicalModels(
            IReadOnlyList<SideToSideBasePhysicalModelSnapshot> oldModels,
            IReadOnlyList<SideToSideBaseModelPlan> targetModels,
            BaseModelReusePlan reuse,
            string positionName)
        {
            foreach (var targetModel in targetModels)
            {
                var old = oldModels.Single(model => model.Kind == targetModel.Kind);
                var action = reuse.ModelActions.Single(candidate =>
                    candidate.PositionNames.Contains(positionName)
                    && string.Equals(
                        candidate.RepresentativeTarget.ModelRoleKey,
                        targetModel.Kind.ToString(),
                        StringComparison.Ordinal));
                if (action.SourceKind == BaseModelSourceKind.Template
                    || !string.Equals(
                        old.ModelPath,
                        action.ExistingModel.ModelPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameKinds(
            IReadOnlyList<SideToSideBasePhysicalModelSnapshot> oldModels,
            IReadOnlyList<SideToSideBaseModelPlan> targetModels)
        {
            return oldModels.Select(model => model.Kind).OrderBy(kind => kind)
                .SequenceEqual(targetModels.Select(model => model.Kind).OrderBy(kind => kind));
        }

        private static IReadOnlyList<SideToSideBaseModelPlan> GetTargetModels(
            SideToSideBasePositionPlan position)
        {
            if (position == null)
            {
                return new SideToSideBaseModelPlan[0];
            }

            return position.ElevatedBaseModel == null
                ? new[] { position.BaseModel }
                : new[] { position.BaseModel, position.ElevatedBaseModel };
        }
    }
}
