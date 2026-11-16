using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal static class BaseModelFirstGenerationPlanner
    {
        internal static BaseModelFirstGenerationPlan Create(
            IEnumerable<BaseModelTarget> targets,
            IReadOnlyList<string> positionOrder)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            var priorities = BuildPriorities(positionOrder);
            var targetArray = targets.ToArray();
            if (targetArray.Any(target => target == null))
            {
                throw new ArgumentException(
                    "首次生成目标不能包含空项。",
                    nameof(targets));
            }

            foreach (var target in targetArray)
            {
                if (!priorities.ContainsKey(target.PositionName))
                {
                    throw new ArgumentException(
                        "首次生成目标包含未知位置：" + target.PositionName,
                        nameof(targets));
                }
            }

            var duplicateTarget = targetArray
                .GroupBy(target => target.PositionRoleKey, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateTarget != null)
            {
                throw new ArgumentException(
                    "首次生成目标包含重复的位置和角色：" + duplicateTarget.Key,
                    nameof(targets));
            }

            var groups = targetArray
                .GroupBy(
                    target => target.Identity.ComparisonKey,
                    StringComparer.Ordinal)
                .Select(group => CreateGroup(group, priorities))
                .OrderBy(
                    group => priorities[group.Representative.PositionName])
                .ThenBy(
                    group => group.Representative.ModelRoleKey,
                    StringComparer.Ordinal)
                .ToArray();
            var targetKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                foreach (var target in group.Targets)
                {
                    targetKeys.Add(target.PositionRoleKey, group.TargetKey);
                }
            }

            return new BaseModelFirstGenerationPlan(groups, targetKeys);
        }

        private static BaseModelFirstGenerationGroup CreateGroup(
            IGrouping<string, BaseModelTarget> group,
            IReadOnlyDictionary<string, int> priorities)
        {
            var orderedTargets = group
                .OrderBy(target => priorities[target.PositionName])
                .ThenBy(target => target.ModelRoleKey, StringComparer.Ordinal)
                .ToArray();
            var representative = orderedTargets[0];
            return new BaseModelFirstGenerationGroup(
                "Template|" + representative.Identity.ComparisonKey,
                representative.Identity,
                representative,
                orderedTargets);
        }

        private static IReadOnlyDictionary<string, int> BuildPriorities(
            IReadOnlyList<string> positionOrder)
        {
            if (positionOrder == null)
            {
                throw new ArgumentNullException(nameof(positionOrder));
            }

            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < positionOrder.Count; index++)
            {
                var positionName = positionOrder[index];
                if (string.IsNullOrWhiteSpace(positionName))
                {
                    throw new ArgumentException(
                        "位置优先级不能包含空项。",
                        nameof(positionOrder));
                }

                if (priorities.ContainsKey(positionName))
                {
                    throw new ArgumentException(
                        "位置优先级包含重复项：" + positionName,
                        nameof(positionOrder));
                }

                priorities.Add(positionName, index);
            }

            return priorities;
        }
    }
}
