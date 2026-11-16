using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 只计算二次生成差异，不访问文件系统或 SolidWorks，便于在任何模型变更前完成校验。
    /// </summary>
    internal static class FaceToFaceFrameSecondaryDrivePlanner
    {
        internal static FaceToFaceFrameSecondaryDrivePlan Create(
            FaceToFaceFrameParameterSnapshot snapshot,
            IReadOnlyList<FaceToFaceFramePositionPlan> currentPlans,
            bool useInstanceIdentity = false)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (currentPlans == null)
            {
                throw new ArgumentNullException(nameof(currentPlans));
            }

            var requestedTargets = currentPlans.ToList();
            ValidateTargetPlans(requestedTargets);
            // SourceName 是同源组的稳定业务名称。模板身份迁移时仍保留该名称，
            // 后续事务会将整组旧组件预拆，并在原目录重建物理模型；禁止在规划层偷偷改成“基架N”。
            var targets = requestedTargets;
            var modelChanges = CreateModelChanges(snapshot, targets);
            var positionChanges = CreatePositionChanges(snapshot.Positions, targets, useInstanceIdentity);

            return new FaceToFaceFrameSecondaryDrivePlan(
                modelChanges,
                positionChanges,
                targets);
        }

        private static IReadOnlyList<FaceToFaceFrameModelChange> CreateModelChanges(
            FaceToFaceFrameParameterSnapshot snapshot,
            IReadOnlyList<FaceToFaceFramePositionPlan> targetPlans)
        {
            var changes = new List<FaceToFaceFrameModelChange>();
            var targetSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 收集所有目标模型，去重后处理。
            var targetModels = GetDistinctTargetModels(targetPlans);
            foreach (var targetModel in targetModels)
            {
                targetSourceNames.Add(targetModel.SourceName);
                // 名称决定物理模型。实例只定位安装位置，独立后的新名称不能借用旧共享模型。
                if (snapshot.Models.TryGetValue(targetModel.SourceName, out var existingModel))
                {
                    // 基架种类是同源模型身份的一部分。模板结构变化不能靠写全局变量原位转换，
                    // 但 SourceName 仍是稳定业务名称，因此规划为 Recreate，而不是自动另起名称。
                    if (existingModel.TemplateIdentity != targetModel.TemplateIdentity
                        || existingModel.FrameTemplateKind != targetModel.FrameTemplateKind
                        || existingModel.IsSmallOffsetFrame != targetModel.IsSmallOffsetFrame)
                    {
                        changes.Add(new FaceToFaceFrameModelChange(
                            FaceToFaceFrameModelChangeKind.Recreate,
                            targetModel.SourceName,
                            existingModel,
                            targetModel));
                        continue;
                    }

                    changes.Add(new FaceToFaceFrameModelChange(
                        existingModel.HasSameParameters(targetModel)
                            ? FaceToFaceFrameModelChangeKind.Keep
                            : FaceToFaceFrameModelChangeKind.DriveExisting,
                        targetModel.SourceName,
                        existingModel,
                        targetModel));
                }
                else
                {
                    changes.Add(new FaceToFaceFrameModelChange(
                        FaceToFaceFrameModelChangeKind.Create,
                        targetModel.SourceName,
                        null,
                        targetModel));
                }
            }

            // 只有最终位置集合对某名称零引用时才产生退役候选；
            // 实际删目录前还会用提交后的真实模型路径再做一次引用计数。
            foreach (var oldModel in GetDistinctSnapshotModels(snapshot.Positions))
            {
                if (!targetSourceNames.Contains(oldModel.SourceName))
                {
                    changes.Add(new FaceToFaceFrameModelChange(
                        FaceToFaceFrameModelChangeKind.RetireCandidate,
                        oldModel.SourceName,
                        oldModel,
                        null));
                }
            }

            return changes.AsReadOnly();
        }
        private static IReadOnlyList<FaceToFaceFramePositionChange> CreatePositionChanges(
            IReadOnlyList<FaceToFaceFramePositionSnapshot> oldPositions,
            IReadOnlyList<FaceToFaceFramePositionPlan> targetPlans,
            bool useInstanceIdentity)
        {
            // 反读入口即使全部目标都是新增，也必须按实例身份计算增删，不能回退到旧位置配对。
            if (useInstanceIdentity || targetPlans.Any(plan => !string.IsNullOrWhiteSpace(plan.ComponentInstanceId)))
            {
                return CreateInstancePositionChanges(oldPositions, targetPlans);
            }

            var changes = new List<FaceToFaceFramePositionChange>();
            var prefixCount = 0;
            while (prefixCount < oldPositions.Count
                && prefixCount < targetPlans.Count
                && HasSameSource(oldPositions[prefixCount], targetPlans[prefixCount]))
            {
                changes.Add(CreateRetainedPositionChange(
                    oldPositions[prefixCount],
                    targetPlans[prefixCount],
                    positionShifted: false));
                prefixCount++;
            }

            var suffixCount = 0;
            while (oldPositions.Count - suffixCount - 1 >= prefixCount
                && targetPlans.Count - suffixCount - 1 >= prefixCount
                && HasSameSource(
                    oldPositions[oldPositions.Count - suffixCount - 1],
                    targetPlans[targetPlans.Count - suffixCount - 1]))
            {
                suffixCount++;
            }

            var oldMiddleCount = oldPositions.Count - prefixCount - suffixCount;
            var targetMiddleCount = targetPlans.Count - prefixCount - suffixCount;
            var pairedMiddleCount = Math.Min(oldMiddleCount, targetMiddleCount);
            for (var offset = 0; offset < pairedMiddleCount; offset++)
            {
                var oldPosition = oldPositions[prefixCount + offset];
                var targetPlan = targetPlans[prefixCount + offset];
                changes.Add(new FaceToFaceFramePositionChange(
                    HasSameSource(oldPosition, targetPlan)
                        ? ResolveRetainedKind(oldPosition, targetPlan, positionShifted: false)
                        : FaceToFaceFramePositionChangeKind.Replace,
                    targetPlan.PositionIndex,
                    oldPosition,
                    targetPlan));
            }

            for (var offset = pairedMiddleCount; offset < oldMiddleCount; offset++)
            {
                var oldPosition = oldPositions[prefixCount + offset];
                changes.Add(new FaceToFaceFramePositionChange(
                    FaceToFaceFramePositionChangeKind.Remove,
                    oldPosition.PositionIndex,
                    oldPosition,
                    null));
            }

            for (var offset = pairedMiddleCount; offset < targetMiddleCount; offset++)
            {
                var targetPlan = targetPlans[prefixCount + offset];
                changes.Add(new FaceToFaceFramePositionChange(
                    FaceToFaceFramePositionChangeKind.Insert,
                    targetPlan.PositionIndex,
                    null,
                    targetPlan));
            }

            for (var offset = suffixCount - 1; offset >= 0; offset--)
            {
                var oldPosition = oldPositions[oldPositions.Count - offset - 1];
                var targetPlan = targetPlans[targetPlans.Count - offset - 1];
                changes.Add(CreateRetainedPositionChange(
                    oldPosition,
                    targetPlan,
                    oldPosition.PositionIndex != targetPlan.PositionIndex));
            }

            return changes.AsReadOnly();
        }

        private static FaceToFaceFramePositionChange CreateRetainedPositionChange(
            FaceToFaceFramePositionSnapshot oldPosition,
            FaceToFaceFramePositionPlan targetPlan,
            bool positionShifted)
        {
            return new FaceToFaceFramePositionChange(
                ResolveRetainedKind(oldPosition, targetPlan, positionShifted),
                targetPlan.PositionIndex,
                oldPosition,
                targetPlan);
        }

        private static FaceToFaceFramePositionChangeKind ResolveRetainedKind(
            FaceToFaceFramePositionSnapshot oldPosition,
            FaceToFaceFramePositionPlan targetPlan,
            bool positionShifted)
        {
            if (!string.Equals(oldPosition.SourceName, targetPlan.SourceName,
                StringComparison.OrdinalIgnoreCase))
            {
                return FaceToFaceFramePositionChangeKind.Replace;
            }

            // 连接段增减后，即使后缀组件本身来源和路由没变，前驱已改变也必须重新配合。
            if (positionShifted || HasCoordinateRouteChanged(oldPosition, targetPlan))
            {
                return FaceToFaceFramePositionChangeKind.Remate;
            }

            return FaceToFaceFramePositionChangeKind.Keep;
        }

        private static bool HasCoordinateRouteChanged(
            FaceToFaceFramePositionSnapshot oldPosition,
            FaceToFaceFramePositionPlan targetPlan)
        {
            if (!string.IsNullOrWhiteSpace(oldPosition.InletCoordinateName))
            {
                // 拐角接口组由左右偏移决定，直接比较实际坐标系名称。
                return !string.Equals(
                        oldPosition.InletCoordinateName,
                        targetPlan.InletCoordinateName,
                        StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(oldPosition.OutletCoordinateName)
                        && !string.Equals(
                        oldPosition.OutletCoordinateName,
                        targetPlan.OutletCoordinateName,
                        StringComparison.Ordinal));
            }

            if (oldPosition.Model.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ||
                targetPlan.ModelPlan.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner)
                throw new InvalidOperationException("拐角基架缺少实际入口坐标系，不能确定装配接口组：" + oldPosition.ComponentInstanceId);

            // 兼容尚未携带实际路由的普通/小偏移旧快照。
            return oldPosition.IsReversed != targetPlan.IsReversed;
        }

        private static bool HasSameSource(
            FaceToFaceFramePositionSnapshot oldPosition,
            FaceToFaceFramePositionPlan targetPlan)
        {
            return string.Equals(
                oldPosition.SourceName,
                targetPlan.SourceName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<FaceToFaceFramePositionChange> CreateInstancePositionChanges(
            IReadOnlyList<FaceToFaceFramePositionSnapshot> oldPositions,
            IReadOnlyList<FaceToFaceFramePositionPlan> targets)
        {
            var oldById = oldPositions.ToDictionary(p => p.ComponentInstanceId, StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<FaceToFaceFramePositionChange>();
            foreach (var target in targets)
            {
                var id = target.ComponentInstanceId;
                if (string.IsNullOrWhiteSpace(id))
                {
                    changes.Add(new FaceToFaceFramePositionChange(
                        FaceToFaceFramePositionChangeKind.Insert, target.PositionIndex, null, target));
                    continue;
                }

                if (!used.Add(id) || !oldById.TryGetValue(id, out var old))
                {
                    throw new ArgumentException("基架安装位置实例重复或已失效：" + id, nameof(targets));
                }

                changes.Add(CreateRetainedPositionChange(old, target, old.PositionIndex != target.PositionIndex));
            }

            foreach (var old in oldPositions.Where(p => !used.Contains(p.ComponentInstanceId)))
            {
                changes.Add(new FaceToFaceFramePositionChange(
                    FaceToFaceFramePositionChangeKind.Remove, old.PositionIndex, old, null));
            }

            return changes.AsReadOnly();
        }

        private static IReadOnlyList<FaceToFaceFrameModelPlan> GetDistinctTargetModels(
            IEnumerable<FaceToFaceFramePositionPlan> targetPlans)
        {
            var models = new List<FaceToFaceFrameModelPlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in targetPlans)
            {
                if (seen.Add(plan.SourceName))
                {
                    models.Add(plan.ModelPlan);
                }
            }

            return models.AsReadOnly();
        }

        private static IReadOnlyList<FaceToFaceFrameModelSnapshot> GetDistinctSnapshotModels(
            IEnumerable<FaceToFaceFramePositionSnapshot> positions)
        {
            var models = new List<FaceToFaceFrameModelSnapshot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in positions)
            {
                if (seen.Add(position.SourceName))
                {
                    models.Add(position.Model);
                }
            }

            return models.AsReadOnly();
        }

        private static void ValidateTargetPlans(
            IReadOnlyList<FaceToFaceFramePositionPlan> targetPlans)
        {
            var modelsBySource = new Dictionary<string, FaceToFaceFrameModelPlan>(
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < targetPlans.Count; index++)
            {
                var plan = targetPlans[index]
                    ?? throw new ArgumentException("当前基架计划不能包含空位置。", nameof(targetPlans));
                if (plan.PositionIndex != index + 1)
                {
                    throw new ArgumentException(
                        $"当前基架计划的位置顺序不连续：期望位置{index + 1}，实际{plan.PositionKey}。",
                        nameof(targetPlans));
                }

                if (!string.Equals(
                    plan.SourceName,
                    plan.ModelPlan.SourceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"位置“{plan.PositionKey}”的基架名称与模型计划不一致。",
                        nameof(targetPlans));
                }

                if (modelsBySource.TryGetValue(plan.SourceName, out var existingPlan))
                {
                    if (!HasSameTargetParameters(existingPlan, plan.ModelPlan))
                    {
                        throw new ArgumentException(
                            $"同一基架名称“{plan.SourceName}”的当前参数不一致。",
                            nameof(targetPlans));
                    }
                }
                else
                {
                    modelsBySource.Add(plan.SourceName, plan.ModelPlan);
                }
            }
        }

        private static bool HasSameTargetParameters(
            FaceToFaceFrameModelPlan first,
            FaceToFaceFrameModelPlan second)
        {
            return string.Equals(first.FolderName, second.FolderName, StringComparison.OrdinalIgnoreCase)
                && FaceToFaceFramePhysicalIdentity.ModelParametersEqual(first, second);
        }
    }
}
