using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Models
{
    // 只由已提交的基座事务产生；不从柜体驱动后的配合差异反推授权范围。
    internal sealed class BaseInstallationChange
    {
        public string Position { get; set; }
        public BaseInstallationState Before { get; set; }
        public BaseInstallationState After { get; set; }
        public BaseInstallationComponent FrameAnchor { get; set; }
        public string BridgeCoordinate { get; set; }
    }

    internal sealed class BaseInstallationState
    {
        public string RootCoordinate { get; set; }
        public BaseInstallationComponent Ordinary { get; set; }
        public BaseInstallationComponent Elevated { get; set; }
    }

    internal sealed class BaseInstallationComponent
    {
        public string Instance { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string Coordinate { get; set; }
    }

    internal static class BaseInstallationChangeFactory
    {
        internal static IReadOnlyList<BaseInstallationChange> Create(
            FaceToFaceBaseSecondarySnapshot before, FaceToFaceBaseSecondarySnapshot after,
            IReadOnlyList<FaceToFaceBasePositionPlan> oldPlans, IReadOnlyList<FaceToFaceBasePositionPlan> newPlans,
            ISet<string> reinstalledPositions)
        {
            var changes = new List<BaseInstallationChange>();
            foreach (var position in oldPlans.Select(p => p.PositionName).Union(newPlans.Select(p => p.PositionName)))
            {
                var oldPlan = oldPlans.SingleOrDefault(p => p.PositionName == position);
                var newPlan = newPlans.SingleOrDefault(p => p.PositionName == position);
                var oldState = Face(before, position, oldPlan);
                var newState = Face(after, position, newPlan);
                if (!Same(oldState, newState) || reinstalledPositions.Contains(position))
                    changes.Add(new BaseInstallationChange { Position = position, Before = oldState, After = newState });
            }
            return changes.AsReadOnly();
        }

        internal static IReadOnlyList<BaseInstallationChange> Create(
            SideToSideBaseSecondarySnapshot before, SideToSideBaseSecondarySnapshot after)
        {
            var changes = new List<BaseInstallationChange>();
            foreach (var position in before.Models.Select(m => m.PositionName).Union(after.Models.Select(m => m.PositionName)))
            {
                var oldState = Side(before, position);
                var newState = Side(after, position);
                if (!Same(oldState, newState))
                    changes.Add(new BaseInstallationChange { Position = position, Before = oldState, After = newState });
            }
            return changes.AsReadOnly();
        }

        private static BaseInstallationState Face(FaceToFaceBaseSecondarySnapshot snapshot, string position,
            FaceToFaceBasePositionPlan plan)
        {
            if (plan == null) return null;
            var ordinary = snapshot.Models.Single(m => m.PositionName == position && m.Kind == FaceToFaceBaseModelKind.Base);
            var elevated = snapshot.Models.SingleOrDefault(m => m.PositionName == position && m.Kind == FaceToFaceBaseModelKind.ElevatedBase);
            return new BaseInstallationState { RootCoordinate = plan.RootCoordinateName,
                Ordinary = Component(ordinary.ComponentFullName, ordinary.ModelPath, "安装坐标系"),
                Elevated = elevated == null ? null : Component(elevated.ComponentFullName, elevated.ModelPath, "下") };
        }

        private static BaseInstallationState Side(SideToSideBaseSecondarySnapshot snapshot, string position)
        {
            var models = snapshot.Models.Where(m => m.PositionName == position).ToArray();
            if (models.Length == 0) return null;
            var ordinary = models.Single(m => m.Kind != SideToSideBaseModelKind.ElevatedBase);
            var elevated = models.SingleOrDefault(m => m.Kind == SideToSideBaseModelKind.ElevatedBase);
            var rootRoute = elevated == null ? SideToSideBaseAssembler.ResolveBase1Route(position)
                : SideToSideBaseAssembler.ResolveElevatedRoute(position);
            var ordinaryCoordinate = elevated == null ? rootRoute.ChildCoordinateName
                : SideToSideBaseAssembler.ResolveBase2Route(position == "A柜前" || position == "A柜后").ChildCoordinateName;
            return new BaseInstallationState { RootCoordinate = rootRoute.ParentCoordinateName,
                Ordinary = Component(ordinary.ComponentFullName, ordinary.ModelPath, ordinaryCoordinate),
                Elevated = elevated == null ? null : Component(elevated.ComponentFullName, elevated.ModelPath, rootRoute.ChildCoordinateName) };
        }

        private static BaseInstallationComponent Component(string instance, string path, string coordinate) =>
            new BaseInstallationComponent { Instance = instance, Path = path, Coordinate = coordinate };

        internal static bool Same(BaseInstallationState a, BaseInstallationState b) => a == null || b == null
            ? a == b : a.RootCoordinate == b.RootCoordinate && Same(a.Ordinary, b.Ordinary) && Same(a.Elevated, b.Elevated);

        internal static bool Same(BaseInstallationComponent a, BaseInstallationComponent b) => a == null || b == null
            ? a == b : a.Instance == b.Instance && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)
                && a.Coordinate == b.Coordinate && a.Configuration == b.Configuration;
    }
}
