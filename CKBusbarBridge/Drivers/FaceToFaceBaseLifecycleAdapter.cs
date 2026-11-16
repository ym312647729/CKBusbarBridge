using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal static class FaceToFaceBaseLifecycleAdapter
    {
        internal static BaseModelFirstGenerationPlan CreateFirstGenerationPlan(
            IReadOnlyList<FaceToFaceBasePositionPlan> positions)
        {
            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            if (positions.Any(position => position == null))
            {
                throw new ArgumentException(
                    "面对面基座计划不能包含空位置。",
                    nameof(positions));
            }

            var positionOrder = positions
                .Select(position => position.PositionName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var targets = new List<BaseModelTarget>();
            foreach (var position in positions)
            {
                AddTarget(targets, position, position.BaseModel);
                if (position.ElevatedBaseModel != null)
                {
                    AddTarget(targets, position, position.ElevatedBaseModel);
                }
            }

            return BaseModelFirstGenerationPlanner.Create(
                targets,
                positionOrder);
        }

        private static void AddTarget(
            ICollection<BaseModelTarget> targets,
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelPlan model)
        {
            var identity = BaseModelIdentity.FromCanonical(
                "FaceToFaceCompatible",
                model.Kind.ToString(),
                model.CacheKey,
                model.CacheKey,
                model.IsTemplateDefault);
            targets.Add(new BaseModelTarget(
                position.PositionName,
                model.Kind.ToString(),
                identity,
                model));
        }
    }
}
