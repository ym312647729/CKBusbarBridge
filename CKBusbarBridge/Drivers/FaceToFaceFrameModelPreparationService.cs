using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToFaceFrameModelPreparationService
    {
        string ResolveAndValidateTemplateAssemblyPath(
            FaceToFaceFrameModelPlan modelPlan);

        string PrepareNewModel(
            string projectPath,
            string frameRoot,
            FaceToFaceFrameModelPlan modelPlan,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceFrameInsulatorParameters insulatorParameters,
            IDictionary<string, string> preparedInsulators);
    }

    /// <summary>
    /// 准备一个独立基架物理模型。首次生成和二次新增基架共用此流程。
    /// </summary>
    internal sealed class FaceToFaceFrameModelPreparationService
        : IFaceToFaceFrameModelPreparationService
    {
        private readonly Func<FaceToFaceFrameModelPlan, string> _resolveTemplateAssemblyPath;
        private readonly IFaceToFaceFrameDriver _driver;
        private readonly IFaceToFaceFrameInsulatorDriver _insulatorDriver;
        private readonly Dictionary<FrameTemplateIdentity, string> _validatedTemplatePaths =
            new Dictionary<FrameTemplateIdentity, string>();

        internal FaceToFaceFrameModelPreparationService(
            Func<FaceToFaceFrameModelPlan, string> resolveTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver)
        {
            _resolveTemplateAssemblyPath = resolveTemplateAssemblyPath
                ?? throw new ArgumentNullException(nameof(resolveTemplateAssemblyPath));
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
        }

        public string ResolveAndValidateTemplateAssemblyPath(
            FaceToFaceFrameModelPlan modelPlan)
        {
            if (modelPlan == null)
            {
                throw new ArgumentNullException(nameof(modelPlan));
            }

            FaceToFaceFrameSourceIdentity.ValidateModelPlanIdentity(modelPlan);
            string fullTemplateAssemblyPath;
            if (!_validatedTemplatePaths.TryGetValue(
                modelPlan.TemplateIdentity,
                out fullTemplateAssemblyPath))
            {
                var templateAssemblyPath = _resolveTemplateAssemblyPath(modelPlan);
                if (string.IsNullOrWhiteSpace(templateAssemblyPath))
                {
                    throw new InvalidOperationException("基架模板总装配体路径不能为空。");
                }

                fullTemplateAssemblyPath = Path.GetFullPath(templateAssemblyPath);
                _validatedTemplatePaths.Add(
                    modelPlan.TemplateIdentity,
                    fullTemplateAssemblyPath);
            }

            if (!File.Exists(fullTemplateAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"基架模板总装配体不存在：{fullTemplateAssemblyPath}",
                    fullTemplateAssemblyPath);
            }

            return fullTemplateAssemblyPath;
        }

        public string PrepareNewModel(
            string projectPath,
            string frameRoot,
            FaceToFaceFrameModelPlan modelPlan,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceFrameInsulatorParameters insulatorParameters,
            IDictionary<string, string> preparedInsulators)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            if (string.IsNullOrWhiteSpace(frameRoot))
            {
                throw new ArgumentException("基架根目录不能为空。", nameof(frameRoot));
            }

            if (modelPlan == null)
            {
                throw new ArgumentNullException(nameof(modelPlan));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            if (modelPlan.InsulatorCount > 0)
            {
                if (insulatorParameters == null)
                {
                    throw new ArgumentNullException(nameof(insulatorParameters));
                }

                if (preparedInsulators == null)
                {
                    throw new ArgumentNullException(nameof(preparedInsulators));
                }
            }

            var fullFrameRoot = Path.GetFullPath(frameRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var finalDirectory = Path.GetFullPath(Path.Combine(
                fullFrameRoot,
                modelPlan.FolderName));
            if (!string.Equals(
                    Path.GetDirectoryName(finalDirectory),
                    fullFrameRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"基架“{modelPlan.SourceName}”的目标目录不是基架根目录的直属子目录：{finalDirectory}");
            }
            var templateAssemblyPath = ResolveAndValidateTemplateAssemblyPath(modelPlan);

            // 新名称始终 PackAndGo 到自己的文件夹，不因为参数相同而复用其他名称的模型。
            var frameAssemblyPath = ValidatePreparedModel(
                _driver.PrepareModel(
                    modelPlan,
                    templateAssemblyPath,
                    finalDirectory,
                    drawingNumbers),
                finalDirectory);
            if (modelPlan.InsulatorCount == 0)
            {
                return frameAssemblyPath;
            }

            PrepareAndAssembleFrameInsulators(
                Path.GetFullPath(projectPath),
                frameAssemblyPath,
                modelPlan,
                insulatorParameters,
                preparedInsulators);
            return frameAssemblyPath;
        }

        private void PrepareAndAssembleFrameInsulators(
            string projectPath,
            string frameAssemblyPath,
            FaceToFaceFrameModelPlan modelPlan,
            FaceToFaceFrameInsulatorParameters insulatorParameters,
            IDictionary<string, string> preparedInsulators)
        {
            var drivePlan = insulatorParameters.CreateDrivePlan(modelPlan);
            string insulatorAssemblyPath = null;
            try
            {
                if (!preparedInsulators.TryGetValue(
                    drivePlan.CacheKey,
                    out insulatorAssemblyPath))
                {
                    insulatorAssemblyPath = ValidatePreparedInsulator(
                        _insulatorDriver.PrepareInsulator(projectPath, drivePlan));
                    preparedInsulators.Add(drivePlan.CacheKey, insulatorAssemblyPath);
                }

                // 内部绝缘子必须在基架进入总装前完成，以免总装引用到半成品。
                _insulatorDriver.AssembleIntoFrame(
                    frameAssemblyPath,
                    insulatorAssemblyPath,
                    modelPlan);
            }
            catch (Exception primaryException)
            {
                FaceToFaceFrameCleanupConfirmation cleanupConfirmation;
                try
                {
                    cleanupConfirmation = _insulatorDriver.CloseDocumentsAfterFailure(
                        frameAssemblyPath,
                        insulatorAssemblyPath);
                }
                catch (Exception cleanupException)
                {
                    cleanupConfirmation = FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "绝缘子装配失败后的文档关闭发生异常：" + cleanupException.Message);
                }

                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    primaryException,
                    cleanupConfirmation,
                    Path.GetDirectoryName(frameAssemblyPath),
                    "基架内部绝缘子装配失败后的文档关闭");
                throw;
            }
        }

        private static string ValidatePreparedModel(
            string preparedModelPath,
            string expectedDirectory)
        {
            if (string.IsNullOrWhiteSpace(preparedModelPath)
                || !File.Exists(preparedModelPath))
            {
                throw new FileNotFoundException(
                    $"基架驱动完成但未找到可装配模型：{preparedModelPath}",
                    preparedModelPath);
            }

            var fullPath = Path.GetFullPath(preparedModelPath);
            if (!FaceToFaceFramePathClassifier.IsPathUnderDirectory(expectedDirectory, fullPath))
            {
                throw new InvalidOperationException(
                    $"基架驱动返回的模型不在已验证的目标目录内：{fullPath}");
            }

            return fullPath;
        }

        private static string ValidatePreparedInsulator(string preparedInsulatorPath)
        {
            if (string.IsNullOrWhiteSpace(preparedInsulatorPath)
                || !File.Exists(preparedInsulatorPath))
            {
                throw new FileNotFoundException(
                    $"绝缘子驱动完成但未找到可装配模型：{preparedInsulatorPath}",
                    preparedInsulatorPath);
            }

            return Path.GetFullPath(preparedInsulatorPath);
        }
    }
}
