using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 根据旧物理引用和最终完整身份，一次性计算四位置基座的复用、拆分与合并。
    /// 此类不访问 SolidWorks、文件系统或图号生成器。
    /// </summary>
    internal static class FaceToFaceBaseSecondaryDrivePlanner
    {
        private static readonly string[] PositionOrder =
        {
            "A柜前",
            "A柜后",
            "B柜前",
            "B柜后"
        };

        internal static FaceToFaceBaseSecondaryDrivePlan Create(
            FaceToFaceBaseSecondarySnapshot snapshot,
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans)
        {
            return Create(snapshot, targetPlans, PositionOrder);
        }

        /// <summary>
        /// 使用调用场景提供的位置顺序生成计划。面对面使用四位置，面对墙使用柜前/柜后，
        /// 其余同源保留、名称归一和结构替换规则完全共用。
        /// </summary>
        internal static FaceToFaceBaseSecondaryDrivePlan Create(
            FaceToFaceBaseSecondarySnapshot snapshot,
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans,
            IReadOnlyList<string> positionOrder)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (targetPlans == null)
            {
                throw new ArgumentNullException(nameof(targetPlans));
            }

            if (positionOrder == null || positionOrder.Count == 0)
            {
                throw new ArgumentException("基座位置顺序不能为空。", nameof(positionOrder));
            }

            ValidateTargetPlans(targetPlans, positionOrder);
            var targets = CreateTargetCandidates(targetPlans, positionOrder);
            var sharedTargets = targets
                .Select(target => new BaseModelTarget(
                    target.Position.PositionName,
                    target.Kind.ToString(),
                    target.Identity.SharedIdentity,
                    target))
                .ToArray();
            var sharedExisting = snapshot.Models
                .Select(model => new BasePhysicalModel(
                    model.PositionName,
                    model.Kind.ToString(),
                    model.ModelPath,
                    model.Identity.SharedIdentity,
                    new BaseModelNameState(
                        model.NameState.TemplateAssemblyBaseName,
                        model.NameState.CurrentAssemblyBaseName,
                        model.NameState.CurrentDrawingNumberSuffix),
                    model,
                    new[] { model.ModelPath }))
                .ToArray();
            var sharedPlan = BaseModelReusePlanner.Create(
                sharedExisting,
                sharedTargets,
                positionOrder);
            var modelActions = sharedPlan.ModelActions
                .Select(ToFaceModelAction)
                .ToArray();
            var positionActions = CreatePositionActions(
                snapshot,
                targetPlans,
                modelActions,
                positionOrder);
            var retirements = sharedPlan.Retirements
                .Select(retirement => new FaceToFaceBaseRetirementCandidate(
                    retirement.ModelPath,
                    retirement.ObsoleteFiles))
                .ToArray();

            return new FaceToFaceBaseSecondaryDrivePlan(
                modelActions,
                positionActions,
                retirements);
        }

        internal static FaceToFaceBaseNameActionKind ResolveNameAction(
            bool hasDrawingNumberSuffix,
            bool targetIsTemplateDefault)
        {
            return ToFaceNameAction(BaseModelNamingPolicy.Resolve(
                hasDrawingNumberSuffix,
                targetIsTemplateDefault));
        }

        private static FaceToFaceBaseNameActionKind ToFaceNameAction(
            BaseModelNameActionKind action)
        {
            switch (action)
            {
                case BaseModelNameActionKind.Keep:
                    return FaceToFaceBaseNameActionKind.Keep;
                case BaseModelNameActionKind.AddDrawingNumber:
                    return FaceToFaceBaseNameActionKind.AddDrawingNumber;
                case BaseModelNameActionKind.RemoveDrawingNumber:
                    return FaceToFaceBaseNameActionKind.RemoveDrawingNumber;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(action),
                        action,
                        null);
            }
        }

        private static FaceToFaceBaseTargetModelAction ToFaceModelAction(
            BaseModelReuseAction action)
        {
            var representative = action.RepresentativeTarget.Context as TargetCandidate;
            if (representative == null)
            {
                throw new InvalidOperationException("面对面目标模型缺少兼容上下文。");
            }

            FaceToFaceBasePhysicalModelSnapshot existingModel = null;
            if (action.ExistingModel != null)
            {
                existingModel = action.ExistingModel.Context
                    as FaceToFaceBasePhysicalModelSnapshot;
                if (existingModel == null)
                {
                    throw new InvalidOperationException(
                        "面对面既有模型缺少兼容上下文。");
                }
            }

            return new FaceToFaceBaseTargetModelAction(
                ToLegacyFaceTargetKey(action, representative),
                representative.Identity,
                ToFaceSource(action.SourceKind),
                existingModel,
                representative.Position,
                representative.Kind,
                action.NeedsDrive,
                ToFaceNameAction(action.NameAction),
                action.PositionNames);
        }

        private static FaceToFaceBaseModelSourceKind ToFaceSource(
            BaseModelSourceKind source)
        {
            switch (source)
            {
                case BaseModelSourceKind.Existing:
                    return FaceToFaceBaseModelSourceKind.Existing;
                case BaseModelSourceKind.Template:
                    return FaceToFaceBaseModelSourceKind.Template;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(source),
                        source,
                        null);
            }
        }

        private static string ToLegacyFaceTargetKey(
            BaseModelReuseAction action,
            TargetCandidate representative)
        {
            if (action.SourceKind == BaseModelSourceKind.Existing)
            {
                var source = action.ExistingModel.Context
                    as FaceToFaceBasePhysicalModelSnapshot;
                if (source == null)
                {
                    throw new InvalidOperationException(
                        "面对面既有模型缺少兼容上下文。");
                }

                return ModelPathKey(source.ModelPath, representative.Kind);
            }

            return "Template|" + representative.Identity.Value;
        }

        private static IReadOnlyList<FaceToFaceBasePositionAction> CreatePositionActions(
            FaceToFaceBaseSecondarySnapshot snapshot,
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans,
            IReadOnlyList<FaceToFaceBaseTargetModelAction> modelActions,
            IReadOnlyList<string> positionOrder)
        {
            var names = snapshot.Models.Select(x => x.PositionName)
                .Concat(targetPlans.Select(x => x.PositionName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => Priority(name, positionOrder))
                .ToArray();
            var actions = new List<FaceToFaceBasePositionAction>();
            foreach (var positionName in names)
            {
                var oldOrdinary = FindOld(snapshot, positionName, FaceToFaceBaseModelKind.Base);
                var oldElevated = FindOld(snapshot, positionName, FaceToFaceBaseModelKind.ElevatedBase);
                var targetPlan = targetPlans.SingleOrDefault(x =>
                    string.Equals(x.PositionName, positionName, StringComparison.Ordinal));
                var ordinaryAction = FindTargetAction(
                    modelActions,
                    positionName,
                    FaceToFaceBaseModelKind.Base);
                var elevatedAction = FindTargetAction(
                    modelActions,
                    positionName,
                    FaceToFaceBaseModelKind.ElevatedBase);

                FaceToFaceBasePositionActionKind kind;
                if (targetPlan == null)
                {
                    kind = FaceToFaceBasePositionActionKind.Remove;
                }
                else if (oldOrdinary == null && oldElevated == null)
                {
                    kind = FaceToFaceBasePositionActionKind.Insert;
                }
                else if (KeepsSamePhysicalSource(oldOrdinary, ordinaryAction)
                    && KeepsSamePhysicalSource(oldElevated, elevatedAction))
                {
                    kind = FaceToFaceBasePositionActionKind.Keep;
                }
                else
                {
                    kind = FaceToFaceBasePositionActionKind.Replace;
                }

                actions.Add(new FaceToFaceBasePositionAction(
                    positionName,
                    kind,
                    targetPlan,
                    oldOrdinary,
                    oldElevated,
                    ordinaryAction?.TargetKey,
                    elevatedAction?.TargetKey));
            }

            return actions;
        }

        private static bool KeepsSamePhysicalSource(
            FaceToFaceBasePhysicalModelSnapshot oldModel,
            FaceToFaceBaseTargetModelAction targetAction)
        {
            if (oldModel == null || targetAction == null)
            {
                return oldModel == null && targetAction == null;
            }

            return targetAction.SourceKind == FaceToFaceBaseModelSourceKind.Existing
                && string.Equals(
                    oldModel.ModelPath,
                    targetAction.ExistingModel.ModelPath,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static FaceToFaceBasePhysicalModelSnapshot FindOld(
            FaceToFaceBaseSecondarySnapshot snapshot,
            string positionName,
            FaceToFaceBaseModelKind kind)
        {
            return snapshot.Models.SingleOrDefault(x =>
                x.Kind == kind
                && string.Equals(x.PositionName, positionName, StringComparison.Ordinal));
        }

        private static FaceToFaceBaseTargetModelAction FindTargetAction(
            IReadOnlyList<FaceToFaceBaseTargetModelAction> actions,
            string positionName,
            FaceToFaceBaseModelKind kind)
        {
            return actions.SingleOrDefault(x =>
                x.Kind == kind
                && x.PositionNames.Contains(positionName));
        }

        private static TargetCandidate[] CreateTargetCandidates(
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans,
            IReadOnlyList<string> positionOrder)
        {
            return targetPlans
                .OrderBy(x => Priority(x.PositionName, positionOrder))
                .SelectMany(position => CreateTargetCandidates(position))
                .ToArray();
        }

        private static IEnumerable<TargetCandidate> CreateTargetCandidates(
            FaceToFaceBasePositionPlan position)
        {
            yield return new TargetCandidate(
                position,
                FaceToFaceBaseModelKind.Base,
                FaceToFaceBaseModelIdentity.FromPlan(
                    position.BaseModel,
                    position.Insulator.TargetBaseKind == FaceToFaceBaseModelKind.Base
                        ? position.Insulator
                        : null));

            if (position.ElevatedBaseModel != null)
            {
                yield return new TargetCandidate(
                    position,
                    FaceToFaceBaseModelKind.ElevatedBase,
                    FaceToFaceBaseModelIdentity.FromPlan(
                        position.ElevatedBaseModel,
                        position.Insulator.TargetBaseKind == FaceToFaceBaseModelKind.ElevatedBase
                            ? position.Insulator
                            : null));
            }
        }

        private static void ValidateTargetPlans(
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans,
            IReadOnlyList<string> positionOrder)
        {
            var duplicate = targetPlans
                .GroupBy(x => x.PositionName, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new ArgumentException(
                    $"最终基座计划存在重复位置：{duplicate.Key}",
                    nameof(targetPlans));
            }

            var unknown = targetPlans.FirstOrDefault(x =>
                Priority(x.PositionName, positionOrder) == int.MaxValue);
            if (unknown != null)
            {
                throw new ArgumentException(
                    $"当前场景不支持基座位置：{unknown.PositionName}",
                    nameof(targetPlans));
            }
        }

        private static string ModelPathKey(string path, FaceToFaceBaseModelKind kind)
        {
            return kind + "|" + Path.GetFullPath(path);
        }

        private static int Priority(
            string positionName,
            IReadOnlyList<string> positionOrder)
        {
            for (var index = 0; index < positionOrder.Count; index++)
            {
                if (string.Equals(
                    positionOrder[index],
                    positionName,
                    StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        private sealed class TargetCandidate
        {
            internal TargetCandidate(
                FaceToFaceBasePositionPlan position,
                FaceToFaceBaseModelKind kind,
                FaceToFaceBaseModelIdentity identity)
            {
                Position = position;
                Kind = kind;
                Identity = identity;
            }

            internal FaceToFaceBasePositionPlan Position { get; }

            internal FaceToFaceBaseModelKind Kind { get; }

            internal FaceToFaceBaseModelIdentity Identity { get; }
        }

    }
}
