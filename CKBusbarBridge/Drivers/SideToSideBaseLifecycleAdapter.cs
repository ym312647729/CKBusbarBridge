using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal static class SideToSideBaseLifecycleAdapter
    {
        internal static readonly IReadOnlyList<string> PositionOrder =
            Array.AsReadOnly(new[]
            {
                "A柜前", "A柜后", "B柜前", "B柜后"
            });

        internal static BaseModelFirstGenerationPlan CreateFirstGenerationPlan(
            IReadOnlyList<SideToSideBasePositionPlan> positions)
        {
            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            var targets = CreateTargets(positions);
            return BaseModelFirstGenerationPlanner.Create(targets, PositionOrder);
        }

        internal static IReadOnlyList<BaseModelTarget> CreateTargets(
            IReadOnlyList<SideToSideBasePositionPlan> positions)
        {
            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            var targets = new List<BaseModelTarget>();
            foreach (var position in positions)
            {
                if (position == null)
                {
                    throw new ArgumentException(
                        "侧对侧基座计划不能包含空位置。",
                        nameof(positions));
                }

                AddTarget(targets, position, position.BaseModel);
                if (position.ElevatedBaseModel != null)
                {
                    AddTarget(targets, position, position.ElevatedBaseModel);
                }
            }

            return targets;
        }

        internal static BaseModelIdentity CreateIdentity(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelPlan model)
        {
            if (position == null)
            {
                throw new ArgumentNullException(nameof(position));
            }

            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var placements = position.Insulators
                .Where(placement => placement.HostModelKind == model.Kind)
                .ToArray();
            var baseState = model.GlobalVariables.Select(pair =>
                new KeyValuePair<string, object>(pair.Key, pair.Value));
            return BaseModelIdentity.Create(
                "SideToSide",
                model.Kind.ToString(),
                baseState,
                BuildHostedState(placements),
                model.IsTemplateDefault);
        }

        private static void AddTarget(
            ICollection<BaseModelTarget> targets,
            SideToSideBasePositionPlan position,
            SideToSideBaseModelPlan model)
        {
            targets.Add(new BaseModelTarget(
                position.PositionName,
                model.Kind.ToString(),
                CreateIdentity(position, model),
                model));
        }

        private static IEnumerable<KeyValuePair<string, object>> BuildHostedState(
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements)
        {
            var state = new List<KeyValuePair<string, object>>();
            for (var index = 0; index < placements.Count; index++)
            {
                var placement = placements[index];
                var prefix = "Insulator[" + index + "].";
                state.Add(Pair(
                    prefix + "HostModelKind",
                    placement.HostModelKind.ToString()));
                state.Add(Pair(
                    prefix + "HostCoordinateName",
                    placement.HostCoordinateName));
                state.Add(Pair(
                    prefix + "ChildCoordinateName",
                    placement.ChildCoordinateName));
                state.Add(Pair(
                    prefix + "PlacementDimensionName",
                    placement.PlacementDimensionName));
                state.Add(Pair(
                    prefix + "PlacementHeight",
                    placement.PlacementHeight));
                if (placement.Layout != null)
                    foreach (var variable in placement.Layout.Variables())
                        state.Add(Pair(prefix + variable.Key, variable.Value));
            }

            return state;
        }

        private static KeyValuePair<string, object> Pair(
            string key,
            object value)
        {
            return new KeyValuePair<string, object>(key, value);
        }
    }
}
