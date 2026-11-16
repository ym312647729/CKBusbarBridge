using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对墙基架生成入口：先一次性规划，再复用面对面的 Pack and Go、绝缘子内部装配和回滚。
    /// 三类基架按有效支撑计划驱动并装配绝缘子；无合法布局的大基架数量为0。
    /// </summary>
    internal sealed class FaceToWallFrameGenerationService
    {
        internal const string FrontOrdinaryBasePositionName = "柜前";
        internal const string RearOrdinaryBasePositionName = "柜后";

        private readonly FaceToFaceFrameGenerationService _generationService;

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string largeTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                regularTemplateAssemblyPath,
                largeTemplateAssemblyPath,
                driver,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string largeTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                regularTemplateAssemblyPath,
                largeTemplateAssemblyPath,
                driver,
                null,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string largeTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                regularTemplateAssemblyPath,
                largeTemplateAssemblyPath,
                driver,
                insulatorDriver,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string largeTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                regularTemplateAssemblyPath,
                largeTemplateAssemblyPath,
                largeTemplateAssemblyPath,
                driver,
                insulatorDriver,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string large1TemplateAssemblyPath,
            string large2TemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                regularTemplateAssemblyPath,
                large1TemplateAssemblyPath,
                large2TemplateAssemblyPath,
                driver,
                null,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string large1TemplateAssemblyPath,
            string large2TemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                regularTemplateAssemblyPath,
                large1TemplateAssemblyPath,
                large2TemplateAssemblyPath,
                driver,
                null,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string large1TemplateAssemblyPath,
            string large2TemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                regularTemplateAssemblyPath,
                large1TemplateAssemblyPath,
                large2TemplateAssemblyPath,
                driver,
                insulatorDriver,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToWallFrameGenerationService(
            string regularTemplateAssemblyPath,
            string large1TemplateAssemblyPath,
            string large2TemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
        {
            if (string.IsNullOrWhiteSpace(regularTemplateAssemblyPath))
            {
                throw new ArgumentException(
                    "常规基架模板总装配体路径不能为空。",
                    nameof(regularTemplateAssemblyPath));
            }

            if (string.IsNullOrWhiteSpace(large1TemplateAssemblyPath))
            {
                throw new ArgumentException(
                    "大基架1模板总装配体路径不能为空。",
                    nameof(large1TemplateAssemblyPath));
            }

            if (string.IsNullOrWhiteSpace(large2TemplateAssemblyPath))
            {
                throw new ArgumentException(
                    "大基架2模板总装配体路径不能为空。",
                    nameof(large2TemplateAssemblyPath));
            }

            // 首次生成和二次新增必须走同一个模板路由，避免两条链路对大基架1/2选择不一致。
            Func<FaceToFaceFrameModelPlan, string> resolveTemplate = plan =>
                ResolveTemplateAssemblyPath(
                    plan,
                    regularTemplateAssemblyPath,
                    large1TemplateAssemblyPath,
                    large2TemplateAssemblyPath);

            _generationService = insulatorDriver == null
                ? new FaceToFaceFrameGenerationService(
                    resolveTemplate,
                    driver,
                    assembler,
                    directoryCleaner)
                : new FaceToFaceFrameGenerationService(
                    resolveTemplate,
                    driver,
                    insulatorDriver,
                    assembler,
                    directoryCleaner);
        }

        internal IReadOnlyList<FaceToFaceFrameAssemblyToken> Generate(
            string projectPath,
            IEnumerable<FaceToWallFramePlanInput> inputs,
            string cabinetOrdinaryBasePositionName,
            IDrawingNumberGenerator drawingNumbers,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            FaceToFaceFrameInsulatorParameters insulatorParameters = null)
        {
            return Generate(
                projectPath,
                inputs,
                cabinetOrdinaryBasePositionName,
                drawingNumbers,
                regularFrameWidth,
                wallCabinetEccentricity,
                0d,
                insulatorParameters);
        }

        internal IReadOnlyList<FaceToFaceFrameAssemblyToken> Generate(
            string projectPath,
            IEnumerable<FaceToWallFramePlanInput> inputs,
            string cabinetOrdinaryBasePositionName,
            IDrawingNumberGenerator drawingNumbers,
            double regularFrameWidth,
            double wallCabinetEccentricity,
            double baseOpeningHeight,
            FaceToFaceFrameInsulatorParameters insulatorParameters = null)
        {
            // 任何磁盘或 SolidWorks 操作前先算出全部模型计划和位置计划。
            var positionPlans = FaceToWallFrameDrivePlanner.CreatePlans(
                inputs,
                regularFrameWidth,
                wallCabinetEccentricity,
                baseOpeningHeight);
            if (string.IsNullOrWhiteSpace(cabinetOrdinaryBasePositionName))
            {
                throw new ArgumentException(
                    "柜体普通基座位置名称不能为空。",
                    nameof(cabinetOrdinaryBasePositionName));
            }

            return _generationService.GeneratePrepared(
                projectPath,
                positionPlans,
                cabinetOrdinaryBasePositionName,
                drawingNumbers,
                insulatorParameters);
        }

        internal static string ResolveOrdinaryBasePositionName(bool cabinetFrontHoleEnabled)
        {
            return cabinetFrontHoleEnabled
                ? FrontOrdinaryBasePositionName
                : RearOrdinaryBasePositionName;
        }

        /// <summary>
        /// 按模型计划选择面对墙基架模板。首次生成与二次局部新增共用此入口。
        /// </summary>
        internal static string ResolveTemplateAssemblyPath(
            FaceToFaceFrameModelPlan plan,
            string regularTemplateAssemblyPath,
            string large1TemplateAssemblyPath,
            string large2TemplateAssemblyPath)
        {
            if (plan == null)
            {
                return regularTemplateAssemblyPath;
            }

            switch (plan.FrameTemplateKind)
            {
                case FaceToWallFrameTemplateKind.Regular:
                    return regularTemplateAssemblyPath;
                case FaceToWallFrameTemplateKind.Large1:
                    return large1TemplateAssemblyPath;
                case FaceToWallFrameTemplateKind.Large2:
                    return large2TemplateAssemblyPath;
                default:
                    // 非法枚举值不能静默回退到常规模板，否则模型身份与实际文件会不一致。
                    throw new ArgumentOutOfRangeException(
                        nameof(plan),
                        plan.FrameTemplateKind,
                        "面对墙基架模板类型无效。");
            }
        }

        /// <summary>
        /// 面对墙首基架定位使用总装根「柜前/柜后安装坐标系」，与位置目录名不是同一字符串。
        /// </summary>
        internal static string ResolveOrdinaryBaseRootCoordinateName(bool cabinetFrontHoleEnabled)
        {
            return cabinetFrontHoleEnabled
                ? FaceToWallBaseDrivePlanner.FrontRootCoordinateName
                : FaceToWallBaseDrivePlanner.RearRootCoordinateName;
        }
    }
}
