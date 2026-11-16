using CKBusbarBridge.Models;
using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 已有物理基座的文件名只允许按已确认的完整图号后缀变换。
    /// </summary>
    internal static class FaceToFaceBaseFileNamePolicy
    {
        internal static string ResolveCurrentSuffix(
            string templateAssemblyBaseName,
            string currentAssemblyBaseName)
        {
            return BaseModelFileNamePolicy.ResolveCurrentSuffix(
                templateAssemblyBaseName,
                currentAssemblyBaseName);
        }

        internal static string ReplaceSuffix(
            string currentBaseName,
            string currentSuffix,
            string targetSuffix)
        {
            return BaseModelFileNamePolicy.ReplaceSuffix(
                currentBaseName,
                currentSuffix,
                targetSuffix);
        }
    }

    internal interface IFaceToFaceBaseExistingModelUpdater
    {
        void UpdateExistingModel(
            string modelPath,
            FaceToFaceBaseModelPlan modelPlan,
            FaceToFaceBaseInsulatorPlan hostedInsulatorPlan);
    }

    /// <summary>
    /// 已有基座二次驱动。默认保持面对面场景的“基座后绝缘子”顺序；
    /// 面对墙场景可切换为“绝缘子先准备/驱动，基座后驱动”的局部驱动顺序。
    /// </summary>
    internal sealed class FaceToFaceBaseExistingModelUpdater
        : IFaceToFaceBaseExistingModelUpdater
    {
        private readonly IFaceToFaceBaseDriver _baseDriver;
        private readonly IFaceToFaceBaseExistingInsulatorDriver _insulatorDriver;
        private readonly string _projectPath;
        private readonly Func<string, FaceToFaceBaseInsulatorPlan, bool> _canDriveInPlace;
        private readonly bool _driveInsulatorBeforeBase;
        private readonly bool _driveBase;

        internal FaceToFaceBaseExistingModelUpdater(
            IFaceToFaceBaseDriver baseDriver,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            string projectPath,
            Func<string, FaceToFaceBaseInsulatorPlan, bool> canDriveInPlace)
            : this(
                baseDriver,
                insulatorDriver,
                projectPath,
                canDriveInPlace,
                driveInsulatorBeforeBase: false,
                driveBase: true)
        {
        }

        internal FaceToFaceBaseExistingModelUpdater(
            IFaceToFaceBaseDriver baseDriver,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            string projectPath,
            Func<string, FaceToFaceBaseInsulatorPlan, bool> canDriveInPlace,
            bool driveInsulatorBeforeBase)
            : this(
                baseDriver,
                insulatorDriver,
                projectPath,
                canDriveInPlace,
                driveInsulatorBeforeBase,
                driveBase: true)
        {
        }

        internal FaceToFaceBaseExistingModelUpdater(
            IFaceToFaceBaseDriver baseDriver,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            string projectPath,
            Func<string, FaceToFaceBaseInsulatorPlan, bool> canDriveInPlace,
            bool driveInsulatorBeforeBase,
            bool driveBase)
        {
            _baseDriver = baseDriver ?? throw new ArgumentNullException(nameof(baseDriver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
            _projectPath = string.IsNullOrWhiteSpace(projectPath)
                ? throw new ArgumentException("项目路径不能为空。", nameof(projectPath))
                : projectPath;
            _canDriveInPlace = canDriveInPlace
                ?? throw new ArgumentNullException(nameof(canDriveInPlace));
            _driveInsulatorBeforeBase = driveInsulatorBeforeBase;
            _driveBase = driveBase;
        }

        public void UpdateExistingModel(
            string modelPath,
            FaceToFaceBaseModelPlan modelPlan,
            FaceToFaceBaseInsulatorPlan hostedInsulatorPlan)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("已有基座路径不能为空。", nameof(modelPath));
            }

            if (modelPlan == null)
            {
                throw new ArgumentNullException(nameof(modelPlan));
            }

            if (hostedInsulatorPlan == null)
            {
                if (_driveBase)
                {
                    _baseDriver.DriveExistingModel(modelPath, modelPlan);
                }
                return;
            }

            if (_driveInsulatorBeforeBase)
            {
                UpdateInsulatorBeforeBase(modelPath, modelPlan, hostedInsulatorPlan);
                return;
            }

            if (_driveBase)
            {
                _baseDriver.DriveExistingModel(modelPath, modelPlan);
            }

            string componentFullName;
            string currentInsulatorPath;
            var hasCurrent = _insulatorDriver.TryGetHostedInsulator(
                modelPath,
                out componentFullName,
                out currentInsulatorPath);
            if (hasCurrent
                && _canDriveInPlace(currentInsulatorPath, hostedInsulatorPlan))
            {
                _insulatorDriver.DriveExistingInsulator(
                    currentInsulatorPath,
                    hostedInsulatorPlan,
                    modelPlan.Width);
                _insulatorDriver.WritePlacement(modelPath, hostedInsulatorPlan);
                return;
            }

            var preparedPath = _insulatorDriver.PrepareInsulator(
                _projectPath,
                hostedInsulatorPlan,
                modelPlan.Width);
            if (string.IsNullOrWhiteSpace(preparedPath))
            {
                throw new InvalidOperationException("宿主绝缘子准备完成后没有返回模型路径。");
            }

            if (hasCurrent)
            {
                // 共享绝缘子不能在原文件上驱动；先删除宿主内旧配合和组件，再装入隔离副本。
                _insulatorDriver.RemoveHostedInsulator(modelPath, componentFullName);
            }

            _insulatorDriver.AssembleIntoHostBase(
                modelPath,
                preparedPath,
                hostedInsulatorPlan);
        }

        private void UpdateInsulatorBeforeBase(
            string modelPath,
            FaceToFaceBaseModelPlan modelPlan,
            FaceToFaceBaseInsulatorPlan hostedInsulatorPlan)
        {
            string componentFullName;
            string currentInsulatorPath;
            var hasCurrent = _insulatorDriver.TryGetHostedInsulator(
                modelPath,
                out componentFullName,
                out currentInsulatorPath);
            if (hasCurrent
                && _canDriveInPlace(currentInsulatorPath, hostedInsulatorPlan))
            {
                // 面对墙的固定顺序：先完成绝缘子模型驱动，再驱动宿主基座并回写定位。
                _insulatorDriver.DriveExistingInsulator(
                    currentInsulatorPath,
                    hostedInsulatorPlan,
                    modelPlan.Width);
                if (_driveBase)
                {
                    _baseDriver.DriveExistingModel(modelPath, modelPlan);
                }
                _insulatorDriver.WritePlacement(modelPath, hostedInsulatorPlan);
                return;
            }

            // 共享绝缘子不能原位驱动时先准备隔离模型；宿主引用的删除和回装必须等基座驱动完成。
            var preparedPath = _insulatorDriver.PrepareInsulator(
                _projectPath,
                hostedInsulatorPlan,
                modelPlan.Width);
            if (string.IsNullOrWhiteSpace(preparedPath))
            {
                throw new InvalidOperationException("宿主绝缘子准备完成后没有返回模型路径。");
            }

            if (_driveBase)
            {
                _baseDriver.DriveExistingModel(modelPath, modelPlan);
            }
            if (hasCurrent)
            {
                _insulatorDriver.RemoveHostedInsulator(modelPath, componentFullName);
            }

            _insulatorDriver.AssembleIntoHostBase(
                modelPath,
                preparedPath,
                hostedInsulatorPlan);
        }
    }
}
