using CKBusbarBridge.models;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToWallSpecialBusbarPhysicalSpecExecutor
    {
        void DriveStandard(
            IModelDoc2 assemblyModel,
            double width,
            double thickness);
    }

    internal interface IFaceToWallSpecialBusbarPhysicalSpecStrategy
    {
        FaceToWallPhaseSpecialTemplateKind TemplateKind { get; }

        void Drive(
            IFaceToWallSpecialBusbarPhysicalSpecExecutor executor,
            IModelDoc2 assemblyModel,
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness);
    }

    internal sealed class VerticalBendPhysicalSpecStrategy
        : IFaceToWallSpecialBusbarPhysicalSpecStrategy
    {
        public FaceToWallPhaseSpecialTemplateKind TemplateKind =>
            FaceToWallPhaseSpecialTemplateKind.VerticalBend;

        public void Drive(
            IFaceToWallSpecialBusbarPhysicalSpecExecutor executor,
            IModelDoc2 assemblyModel,
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(nameof(executor));
            }

            executor.DriveStandard(assemblyModel, width, thickness);
        }
    }

    internal sealed class OffsetConnectionPhysicalSpecStrategy
        : IFaceToWallSpecialBusbarPhysicalSpecStrategy
    {
        public FaceToWallPhaseSpecialTemplateKind TemplateKind =>
            FaceToWallPhaseSpecialTemplateKind.OffsetConnection;

        public void Drive(
            IFaceToWallSpecialBusbarPhysicalSpecExecutor executor,
            IModelDoc2 assemblyModel,
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(nameof(executor));
            }

            executor.DriveStandard(assemblyModel, width, thickness);
        }
    }

    internal sealed class MilledBarPhysicalSpecStrategy
        : IFaceToWallSpecialBusbarPhysicalSpecStrategy
    {
        public FaceToWallPhaseSpecialTemplateKind TemplateKind =>
            FaceToWallPhaseSpecialTemplateKind.MilledBar;

        public void Drive(
            IFaceToWallSpecialBusbarPhysicalSpecExecutor executor,
            IModelDoc2 assemblyModel,
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(nameof(executor));
            }

            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var offsetSize = plan.OffsetSize;
            if (double.IsNaN(offsetSize) ||
                double.IsInfinity(offsetSize) ||
                offsetSize < 0)
            {
                throw new InvalidOperationException(
                    string.Format("铣排偏移量必须为有限非负数：{0}。", offsetSize));
            }

            var physicalWidth = width + offsetSize;
            if (double.IsNaN(physicalWidth) ||
                double.IsInfinity(physicalWidth) ||
                physicalWidth <= 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "铣排实际排宽必须为有限正数：排宽={0}，偏移量={1}。",
                        width,
                        offsetSize));
            }

            executor.DriveStandard(assemblyModel, physicalWidth, thickness);
        }
    }

    internal sealed class StraightRaiseOnlyPhysicalSpecStrategy
        : IFaceToWallSpecialBusbarPhysicalSpecStrategy
    {
        public FaceToWallPhaseSpecialTemplateKind TemplateKind =>
            FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly;

        public void Drive(
            IFaceToWallSpecialBusbarPhysicalSpecExecutor executor,
            IModelDoc2 assemblyModel,
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(nameof(executor));
            }

            executor.DriveStandard(assemblyModel, width, thickness);
        }
    }

    internal sealed class FaceToWallSpecialBusbarPhysicalSpecStrategyRegistry
    {
        private readonly IReadOnlyDictionary<
            FaceToWallPhaseSpecialTemplateKind,
            IFaceToWallSpecialBusbarPhysicalSpecStrategy> strategies;

        private FaceToWallSpecialBusbarPhysicalSpecStrategyRegistry(
            IEnumerable<IFaceToWallSpecialBusbarPhysicalSpecStrategy> strategies)
        {
            if (strategies == null)
            {
                throw new ArgumentNullException(nameof(strategies));
            }

            var strategyList = strategies.ToList();
            if (strategyList.Any(item => item == null))
            {
                throw new ArgumentException("面对墙特殊铜排实际规格策略不能为空。", nameof(strategies));
            }

            var duplicateKind = strategyList
                .GroupBy(item => item.TemplateKind)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateKind != null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "面对墙特殊铜排实际规格策略重复注册：{0}。",
                        duplicateKind.Key));
            }

            this.strategies = strategyList.ToDictionary(
                item => item.TemplateKind,
                item => item);
        }

        public static FaceToWallSpecialBusbarPhysicalSpecStrategyRegistry CreateDefault()
        {
            return new FaceToWallSpecialBusbarPhysicalSpecStrategyRegistry(
                new IFaceToWallSpecialBusbarPhysicalSpecStrategy[]
                {
                    new VerticalBendPhysicalSpecStrategy(),
                    new OffsetConnectionPhysicalSpecStrategy(),
                    new MilledBarPhysicalSpecStrategy(),
                    new StraightRaiseOnlyPhysicalSpecStrategy()
                });
        }

        public IFaceToWallSpecialBusbarPhysicalSpecStrategy Resolve(
            FaceToWallPhaseSpecialTemplateKind kind)
        {
            if (!strategies.TryGetValue(kind, out var strategy))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "未注册面对墙特殊铜排实际规格策略：{0}。",
                        kind));
            }

            return strategy;
        }
    }
}
