using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对墙独立基座规划入口。只负责两位置和相间距字段映射，公式复用共享 CreatePositionPlan。
    /// </summary>
    internal static class FaceToWallBaseDrivePlanner
    {
        internal const string FrontPositionName = "柜前";
        internal const string RearPositionName = "柜后";
        internal const string FrontRootCoordinateName = "柜前安装坐标系";
        internal const string RearRootCoordinateName = "柜后安装坐标系";

        /// <summary>
        /// 只返回已启用柜孔，顺序固定为柜前、柜后。
        /// 总装根坐标系使用「柜前/柜后安装坐标系」，与位置目录名不是同一字符串。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            FaceToWallCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var insulatorInput = StandaloneBaseInsulatorInput.From(parameters.General);
            var busbarThickness = FaceToFaceBusbarCenterDistanceHelper
                .ResolveBusbarThickness(insulatorInput.BusbarSpec);
            var candidates = new[]
            {
                new PositionCandidate(
                    FrontPositionName,
                    FrontRootCoordinateName,
                    StandaloneBaseHoleInput.From(parameters.Cabinet?.FrontHole, parameters.Cabinet?.CabinetWidth ?? 0)),
                new PositionCandidate(
                    RearPositionName,
                    RearRootCoordinateName,
                    StandaloneBaseHoleInput.From(parameters.Cabinet?.BackHole, parameters.Cabinet?.CabinetWidth ?? 0))
            };

            var plans = new List<FaceToFaceBasePositionPlan>();
            foreach (var candidate in candidates)
            {
                if (candidate.Hole == null || !candidate.Hole.IsEnabled)
                {
                    continue;
                }

                if (!NoPhaseChangeBaseOpeningHeightRules.TryValidate(
                        candidate.Hole.BaseOpeningHeight,
                        insulatorInput.InsulatorHeight,
                        busbarThickness,
                        insulatorInput.SinglePhaseRootCount,
                        parameters.General.InsulationDistance,
                        out var validationMessage))
                {
                    throw new ArgumentOutOfRangeException(
                        CabinetHoleParameters.BaseOpeningHeightDisplayName,
                        candidate.Hole.BaseOpeningHeight,
                        candidate.Name + validationMessage);
                }

                plans.Add(FaceToFaceBaseDrivePlanner.CreatePositionPlan(
                    candidate.Name,
                    candidate.RootCoordinateName,
                    candidate.Hole,
                    insulatorInput));
            }

            return plans;
        }

        private sealed class PositionCandidate
        {
            internal PositionCandidate(
                string name,
                string rootCoordinateName,
                StandaloneBaseHoleInput hole)
            {
                Name = name;
                RootCoordinateName = rootCoordinateName;
                Hole = hole;
            }

            internal string Name { get; }

            internal string RootCoordinateName { get; }

            internal StandaloneBaseHoleInput Hole { get; }
        }
    }
}
