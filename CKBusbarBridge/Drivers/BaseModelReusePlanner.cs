using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal enum BaseModelSourceKind
    {
        Existing,
        Template
    }

    internal sealed class BaseModelNameState
    {
        internal BaseModelNameState(
            string templateAssemblyBaseName,
            string currentAssemblyBaseName,
            string currentDrawingNumberSuffix)
        {
            TemplateAssemblyBaseName = RequireText(
                templateAssemblyBaseName,
                nameof(templateAssemblyBaseName));
            CurrentAssemblyBaseName = RequireText(
                currentAssemblyBaseName,
                nameof(currentAssemblyBaseName));
            CurrentDrawingNumberSuffix = currentDrawingNumberSuffix ?? string.Empty;
        }

        internal string TemplateAssemblyBaseName { get; }

        internal string CurrentAssemblyBaseName { get; }

        internal string CurrentDrawingNumberSuffix { get; }

        internal bool HasDrawingNumberSuffix =>
            !string.IsNullOrWhiteSpace(CurrentDrawingNumberSuffix);

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("模型名称不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class BasePhysicalModel
    {
        internal BasePhysicalModel(
            string positionName,
            string modelRoleKey,
            string modelPath,
            BaseModelIdentity identity,
            BaseModelNameState nameState,
            object context,
            IEnumerable<string> obsoleteFiles)
        {
            PositionName = RequireText(positionName, nameof(positionName));
            ModelRoleKey = RequireText(modelRoleKey, nameof(modelRoleKey));
            ModelPath = NormalizeRequiredPath(modelPath, nameof(modelPath));
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            NameState = nameState ?? throw new ArgumentNullException(nameof(nameState));
            Context = context;
            ObsoleteFiles = Array.AsReadOnly((obsoleteFiles
                ?? Enumerable.Empty<string>())
                .Select(path => NormalizeRequiredPath(path, nameof(obsoleteFiles)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        internal string PositionName { get; }

        internal string ModelRoleKey { get; }

        internal string ModelPath { get; }

        internal BaseModelIdentity Identity { get; }

        internal BaseModelNameState NameState { get; }

        internal object Context { get; }

        internal IReadOnlyList<string> ObsoleteFiles { get; }

        internal string PositionRoleKey => PositionName + "|" + ModelRoleKey;

        private static string NormalizeRequiredPath(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("模型路径不能为空。", parameterName);
            }

            return Path.GetFullPath(value);
        }

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("值不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class BaseModelReuseAction
    {
        internal BaseModelReuseAction(
            string targetKey,
            BaseModelIdentity targetIdentity,
            BaseModelSourceKind sourceKind,
            BasePhysicalModel existingModel,
            BaseModelTarget representativeTarget,
            bool needsDrive,
            BaseModelNameActionKind nameAction,
            IEnumerable<string> positionNames)
        {
            TargetKey = string.IsNullOrWhiteSpace(targetKey)
                ? throw new ArgumentException("目标模型键不能为空。", nameof(targetKey))
                : targetKey;
            TargetIdentity = targetIdentity
                ?? throw new ArgumentNullException(nameof(targetIdentity));
            SourceKind = sourceKind;
            ExistingModel = existingModel;
            RepresentativeTarget = representativeTarget
                ?? throw new ArgumentNullException(nameof(representativeTarget));
            NeedsDrive = needsDrive;
            NameAction = nameAction;
            PositionNames = Array.AsReadOnly((positionNames
                ?? throw new ArgumentNullException(nameof(positionNames))).ToArray());

            if (PositionNames.Count == 0)
            {
                throw new ArgumentException(
                    "目标模型至少需要一个引用位置。",
                    nameof(positionNames));
            }

            if (sourceKind == BaseModelSourceKind.Existing && existingModel == null)
            {
                throw new ArgumentException(
                    "复用已有模型时必须提供物理模型。",
                    nameof(existingModel));
            }

            if (sourceKind == BaseModelSourceKind.Template && existingModel != null)
            {
                throw new ArgumentException(
                    "模板生成动作不能携带已有模型。",
                    nameof(existingModel));
            }
        }

        internal string TargetKey { get; }

        internal BaseModelIdentity TargetIdentity { get; }

        internal BaseModelSourceKind SourceKind { get; }

        internal BasePhysicalModel ExistingModel { get; }

        internal BaseModelTarget RepresentativeTarget { get; }

        internal bool NeedsDrive { get; }

        internal BaseModelNameActionKind NameAction { get; }

        internal IReadOnlyList<string> PositionNames { get; }
    }

    internal sealed class BaseModelRetirement
    {
        internal BaseModelRetirement(
            string modelPath,
            IEnumerable<string> obsoleteFiles)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException(
                    "待退役模型路径不能为空。",
                    nameof(modelPath));
            }

            ModelPath = Path.GetFullPath(modelPath);
            ObsoleteFiles = Array.AsReadOnly((obsoleteFiles
                ?? Enumerable.Empty<string>())
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        internal string ModelPath { get; }

        internal IReadOnlyList<string> ObsoleteFiles { get; }
    }

    internal sealed class BaseModelReusePlan
    {
        internal BaseModelReusePlan(
            IEnumerable<BaseModelReuseAction> modelActions,
            IEnumerable<BaseModelRetirement> retirements)
        {
            ModelActions = Array.AsReadOnly((modelActions
                ?? throw new ArgumentNullException(nameof(modelActions))).ToArray());
            Retirements = Array.AsReadOnly((retirements
                ?? throw new ArgumentNullException(nameof(retirements))).ToArray());
        }

        internal IReadOnlyList<BaseModelReuseAction> ModelActions { get; }

        internal IReadOnlyList<BaseModelRetirement> Retirements { get; }
    }

    internal static class BaseModelReusePlanner
    {
        internal static BaseModelReusePlan Create(
            IEnumerable<BasePhysicalModel> existingModels,
            IEnumerable<BaseModelTarget> targets,
            IReadOnlyList<string> positionOrder)
        {
            if (existingModels == null)
            {
                throw new ArgumentNullException(nameof(existingModels));
            }

            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            var priorities = BuildPriorities(positionOrder);
            var existingArray = existingModels.ToArray();
            var targetArray = targets.ToArray();
            EnsureNoNullItems(existingArray, nameof(existingModels));
            EnsureNoNullItems(targetArray, nameof(targets));
            ValidatePositions(existingArray.Select(model => model.PositionName), priorities);
            ValidatePositions(targetArray.Select(target => target.PositionName), priorities);
            EnsureDistinctPositionRoles(
                existingArray.Select(model => model.PositionRoleKey),
                nameof(existingModels));
            EnsureDistinctPositionRoles(
                targetArray.Select(target => target.PositionRoleKey),
                nameof(targets));

            var targetGroups = targetArray
                .GroupBy(
                    target => target.Identity.ComparisonKey,
                    StringComparer.Ordinal)
                .OrderBy(group => group.Min(target =>
                    priorities[target.PositionName]))
                .ThenBy(
                    group => group.First().ModelRoleKey,
                    StringComparer.Ordinal)
                .ToArray();
            var oldGroups = existingArray
                .GroupBy(
                    model => ModelPathKey(
                        model.ModelPath,
                        model.ModelRoleKey),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            EnsureConsistentSourceTemplates(
                oldGroups,
                nameof(existingModels));
            var reservations = ReserveExistingSources(
                oldGroups,
                targetArray,
                priorities);
            var actions = targetGroups
                .Select(group => CreateAction(
                    group,
                    reservations,
                    priorities))
                .ToArray();
            var selectedExistingKeys = new HashSet<string>(
                actions
                    .Where(action =>
                        action.SourceKind == BaseModelSourceKind.Existing)
                    .Select(action => ModelPathKey(
                        action.ExistingModel.ModelPath,
                        action.ExistingModel.ModelRoleKey)),
                StringComparer.OrdinalIgnoreCase);
            var retirements = oldGroups
                .Where(group => !selectedExistingKeys.Contains(group.Key))
                .OrderBy(group => group.Min(model =>
                    priorities[model.PositionName]))
                .ThenBy(
                    group => group.First().ModelRoleKey,
                    StringComparer.Ordinal)
                .Select(group =>
                {
                    var representative = group
                        .OrderBy(model => priorities[model.PositionName])
                        .First();
                    return new BaseModelRetirement(
                        representative.ModelPath,
                        group.SelectMany(model => model.ObsoleteFiles)
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                })
                .ToArray();

            return new BaseModelReusePlan(actions, retirements);
        }

        private static Dictionary<string, ExistingReservation>
            ReserveExistingSources(
                IEnumerable<IGrouping<string, BasePhysicalModel>> oldGroups,
                IReadOnlyList<BaseModelTarget> targets,
                IReadOnlyDictionary<string, int> priorities)
        {
            var reservations = new Dictionary<string, ExistingReservation>(
                StringComparer.Ordinal);
            foreach (var oldGroup in oldGroups)
            {
                var oldModels = oldGroup
                    .OrderBy(model => priorities[model.PositionName])
                    .ToArray();
                var sourceIdentity = oldModels[0].Identity;
                var finalTarget = oldModels
                    .Select(old => targets.FirstOrDefault(target =>
                        string.Equals(
                            target.ModelRoleKey,
                            old.ModelRoleKey,
                            StringComparison.Ordinal)
                        && string.Equals(
                            target.PositionName,
                            old.PositionName,
                            StringComparison.Ordinal)
                        && HasSameSourceTemplate(
                            sourceIdentity,
                            target.Identity)))
                    .FirstOrDefault(target => target != null);
                if (finalTarget == null)
                {
                    continue;
                }

                var candidate = new ExistingReservation(
                    oldModels[0],
                    priorities[finalTarget.PositionName]);
                ExistingReservation current;
                if (!reservations.TryGetValue(
                        finalTarget.Identity.ComparisonKey,
                        out current)
                    || candidate.Priority < current.Priority
                    || (candidate.Priority == current.Priority
                        && string.Compare(
                            candidate.Model.ModelPath,
                            current.Model.ModelPath,
                            StringComparison.OrdinalIgnoreCase) < 0))
                {
                    reservations[finalTarget.Identity.ComparisonKey] = candidate;
                }
            }

            return reservations;
        }

        private static BaseModelReuseAction CreateAction(
            IGrouping<string, BaseModelTarget> group,
            IReadOnlyDictionary<string, ExistingReservation> reservations,
            IReadOnlyDictionary<string, int> priorities)
        {
            var candidates = group
                .OrderBy(target => priorities[target.PositionName])
                .ToArray();
            var representative = candidates[0];
            ExistingReservation reservation;
            if (reservations.TryGetValue(group.Key, out reservation))
            {
                return new BaseModelReuseAction(
                    ModelPathKey(
                        reservation.Model.ModelPath,
                        representative.ModelRoleKey),
                    representative.Identity,
                    BaseModelSourceKind.Existing,
                    reservation.Model,
                    representative,
                    !representative.Identity.Equals(reservation.Model.Identity),
                    BaseModelNamingPolicy.Resolve(
                        reservation.Model.NameState.HasDrawingNumberSuffix,
                        representative.Identity.IsTemplateDefault),
                    candidates.Select(target => target.PositionName));
            }

            return new BaseModelReuseAction(
                "Template|" + representative.Identity.ComparisonKey,
                representative.Identity,
                BaseModelSourceKind.Template,
                null,
                representative,
                true,
                BaseModelNameActionKind.Keep,
                candidates.Select(target => target.PositionName));
        }

        private static IReadOnlyDictionary<string, int> BuildPriorities(
            IReadOnlyList<string> positionOrder)
        {
            if (positionOrder == null)
            {
                throw new ArgumentNullException(nameof(positionOrder));
            }

            if (positionOrder.Count == 0)
            {
                throw new ArgumentException(
                    "基座位置顺序不能为空。",
                    nameof(positionOrder));
            }

            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < positionOrder.Count; index++)
            {
                var positionName = positionOrder[index];
                if (string.IsNullOrWhiteSpace(positionName))
                {
                    throw new ArgumentException(
                        "基座位置顺序不能包含空项。",
                        nameof(positionOrder));
                }

                if (priorities.ContainsKey(positionName))
                {
                    throw new ArgumentException(
                        "基座位置顺序包含重复项：" + positionName,
                        nameof(positionOrder));
                }

                priorities.Add(positionName, index);
            }

            return priorities;
        }

        private static void ValidatePositions(
            IEnumerable<string> positionNames,
            IReadOnlyDictionary<string, int> priorities)
        {
            var unknown = positionNames.FirstOrDefault(positionName =>
                !priorities.ContainsKey(positionName));
            if (unknown != null)
            {
                throw new ArgumentException("当前场景不支持基座位置：" + unknown);
            }
        }

        private static void EnsureDistinctPositionRoles(
            IEnumerable<string> positionRoleKeys,
            string parameterName)
        {
            var duplicate = positionRoleKeys
                .GroupBy(key => key, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new ArgumentException(
                    "基座模型包含重复的位置和角色：" + duplicate.Key,
                    parameterName);
            }
        }

        private static void EnsureConsistentSourceTemplates(
            IEnumerable<IGrouping<string, BasePhysicalModel>> oldGroups,
            string parameterName)
        {
            foreach (var oldGroup in oldGroups)
            {
                var sourceIdentity = oldGroup.First().Identity;
                if (oldGroup.Skip(1).Any(model =>
                    !HasSameSourceTemplate(
                        sourceIdentity,
                        model.Identity)))
                {
                    throw new ArgumentException(
                        "同一物理基座模型不能包含不同的场景或模板身份："
                            + oldGroup.Key,
                        parameterName);
                }
            }
        }

        private static bool HasSameSourceTemplate(
            BaseModelIdentity first,
            BaseModelIdentity second)
        {
            return string.Equals(
                    first.ScenarioKey,
                    second.ScenarioKey,
                    StringComparison.Ordinal)
                && string.Equals(
                    first.TemplateKey,
                    second.TemplateKey,
                    StringComparison.Ordinal);
        }

        private static void EnsureNoNullItems<T>(
            IEnumerable<T> items,
            string parameterName)
            where T : class
        {
            if (items.Any(item => item == null))
            {
                throw new ArgumentException(
                    "基座模型集合不能包含空项。",
                    parameterName);
            }
        }

        private static string ModelPathKey(string path, string modelRoleKey)
        {
            return modelRoleKey + "|" + Path.GetFullPath(path);
        }

        private sealed class ExistingReservation
        {
            internal ExistingReservation(BasePhysicalModel model, int priority)
            {
                Model = model;
                Priority = priority;
            }

            internal BasePhysicalModel Model { get; }

            internal int Priority { get; }
        }
    }
}
