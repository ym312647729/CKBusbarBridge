using CKBusbarBridge.Helpers;

using CKBusbarBridge.Models;

using System;

using System.IO;



namespace CKBusbarBridge.Drivers

{

    /// <summary>

    /// 独立基座/增高基座从共享模板 PackAndGo 到最终目录并完成驱动。

    /// </summary>

    internal static class FaceToFaceBaseModelPreparation

    {

        /// <summary>

        /// 为单个位置准备可直接装配的基座或增高基座模型。

        /// </summary>

        internal static string PrepareNewModel(

            IFaceToFaceBaseTemplateCopier templateCopier,

            IFaceToFaceBaseDriver driver,

            IFaceToFaceBaseInsulatorDriver insulatorDriver,

            string baseRoot,

            string projectPath,

            string positionName,

            FaceToFaceBaseModelPlan modelPlan,

            IDrawingNumberGenerator drawingNumbers,

            FaceToFaceBaseInsulatorPlan hostInsulatorPlan = null,

            string sourceAssemblyPath = null)

        {

            if (templateCopier == null)

            {

                throw new ArgumentNullException(nameof(templateCopier));

            }



            if (driver == null)

            {

                throw new ArgumentNullException(nameof(driver));

            }



            if (insulatorDriver == null)

            {

                throw new ArgumentNullException(nameof(insulatorDriver));

            }



            if (string.IsNullOrWhiteSpace(baseRoot))

            {

                throw new ArgumentException("基座根目录不能为空。", nameof(baseRoot));

            }



            if (string.IsNullOrWhiteSpace(projectPath))

            {

                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));

            }



            if (string.IsNullOrWhiteSpace(positionName))

            {

                throw new ArgumentException("位置名称不能为空。", nameof(positionName));

            }



            if (modelPlan == null)

            {

                throw new ArgumentNullException(nameof(modelPlan));

            }



            if (drawingNumbers == null)

            {

                throw new ArgumentNullException(nameof(drawingNumbers));

            }



            var typeFolderName = modelPlan.Kind == FaceToFaceBaseModelKind.Base

                ? "基座"

                : "增高基座";

            var finalDirectory = Path.Combine(

                baseRoot,

                positionName,

                typeFolderName);

            var templateAssemblyPath = templateCopier.GetTemplateAssemblyPath(modelPlan.Kind);

            var selectedSourceAssemblyPath = string.IsNullOrWhiteSpace(sourceAssemblyPath)

                ? templateAssemblyPath

                : Path.GetFullPath(sourceAssemblyPath);

            var preparedModelPath = driver.PrepareModel(

                modelPlan,

                selectedSourceAssemblyPath,

                finalDirectory,

                drawingNumbers);



            if (string.IsNullOrWhiteSpace(preparedModelPath)

                || !File.Exists(preparedModelPath))

            {

                throw new FileNotFoundException(

                    $"基座驱动完成但未找到可装配模型：{preparedModelPath}",

                    preparedModelPath);

            }



            if (!SamePath(selectedSourceAssemblyPath, templateAssemblyPath))

            {

                // 总装内的模板基座可能已含旧绝缘子；PackAndGo 会把该组件一并复制。

                // 先移除复制件中的旧绝缘子，再继续复用首次生成逻辑装入目标绝缘子。

                RemoveCopiedHostedInsulator(insulatorDriver, preparedModelPath);

            }



            if (hostInsulatorPlan != null)

            {

                var insulatorPath = insulatorDriver.PrepareInsulator(

                    projectPath,

                    hostInsulatorPlan,

                    modelPlan.Width);

                if (!string.IsNullOrWhiteSpace(insulatorPath))

                {

                    insulatorDriver.AssembleIntoHostBase(

                        preparedModelPath,

                        insulatorPath,

                        hostInsulatorPlan);

                }

            }



            return Path.GetFullPath(preparedModelPath);

        }



        private static void RemoveCopiedHostedInsulator(

            IFaceToFaceBaseInsulatorDriver insulatorDriver,

            string preparedModelPath)

        {

            var existingInsulatorDriver =

                insulatorDriver as IFaceToFaceBaseExistingInsulatorDriver;

            if (existingInsulatorDriver == null)

            {

                throw new InvalidOperationException(

                    "从总装模板基座生成新基座时，绝缘子驱动器不支持清理复制件。");

            }



            string componentFullName;

            string ignoredModelPath;

            if (existingInsulatorDriver.TryGetHostedInsulator(

                preparedModelPath,

                out componentFullName,

                out ignoredModelPath))

            {

                existingInsulatorDriver.RemoveHostedInsulator(

                    preparedModelPath,

                    componentFullName);

            }

        }



        private static bool SamePath(string left, string right)

        {

            return string.Equals(

                Path.GetFullPath(left),

                Path.GetFullPath(right),

                StringComparison.OrdinalIgnoreCase);

        }

    }

}

