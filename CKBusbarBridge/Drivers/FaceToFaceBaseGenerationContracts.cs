using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 表示组件已插入总装配体，但失败后的即时删除未得到 SolidWorks 确认。
    /// 编排层收到该异常后必须保留磁盘模型，避免残留组件引用丢失。
    /// </summary>
    internal sealed class FaceToFaceBaseAssemblyCleanupException
        : System.InvalidOperationException
    {
        internal FaceToFaceBaseAssemblyCleanupException(
            string message,
            System.Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// 解析共享模板路径，供 PackAndGo 直接从模板写入最终目录。
    /// </summary>
    internal interface IFaceToFaceBaseTemplateCopier
    {
        string GetTemplateAssemblyPath(FaceToFaceBaseModelKind kind);
    }

    /// <summary>
    /// 将模板或已复制模型准备为可直接装配的最终模型。
    /// </summary>
    internal interface IFaceToFaceBaseDriver
    {
        string PrepareModel(
            FaceToFaceBaseModelPlan plan,
            string sourceAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers);

        /// <summary>
        /// 二次驱动：对已有基座文件写入全局变量并保存，不复制、不取号。
        /// </summary>
        void DriveExistingModel(
            string assemblyPath,
            FaceToFaceBaseModelPlan plan);
    }

    /// <summary>
    /// 准备并驱动可装入独立基座的绝缘子装配体，并在基座驱动阶段将其装入宿主基座。
    /// </summary>
    internal interface IFaceToFaceBaseInsulatorDriver
    {
        /// <summary>
        /// 从共享模板准备并驱动绝缘子装配体，返回可装配路径。
        /// </summary>
        string PrepareInsulator(
            string projectPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth);

        /// <summary>
        /// 将已驱动的绝缘子装入宿主基座装配体：
        /// 写入定位草图尺寸，并以“绝缘子安装坐标系/安装坐标系”做重合配合。
        /// </summary>
        void AssembleIntoHostBase(
            string hostBaseAssemblyPath,
            string insulatorAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan);

        /// <summary>
        /// 二次驱动：只改已装入宿主的绝缘子尺寸，不从模板重新生成。
        /// </summary>
        void DriveExistingInsulator(
            string insulatorAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth);

        /// <summary>
        /// 二次驱动：写入宿主基座内的绝缘子定位草图尺寸。
        /// </summary>
        void WritePlacement(
            string hostBaseAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan);
    }

    /// <summary>
    /// 二次驱动已有基座时所需的宿主绝缘子读取和移除能力。
    /// 单独扩展接口，避免首次生成测试替身被迫实现二次操作。
    /// </summary>
    internal interface IFaceToFaceBaseExistingInsulatorDriver
        : IFaceToFaceBaseInsulatorDriver
    {
        bool TryGetHostedInsulator(
            string hostBaseAssemblyPath,
            out string componentFullName,
            out string modelPath);

        void RemoveHostedInsulator(
            string hostBaseAssemblyPath,
            string componentFullName);
    }

    /// <summary>
    /// 向事务层公开绝缘子输出目录的纯计算结果，使复制/驱动前即可决定备份还是登记新目录。
    /// </summary>
    internal interface IFaceToFaceBaseInsulatorOutputResolver
    {
        string ResolveOutputDirectory(
            string projectPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth);
    }

    /// <summary>
    /// 抽象总装配体插入和配合操作，使流程编排可脱离 SolidWorks 进行测试。
    /// 绝缘子已在基座驱动阶段装入对应基座装配体，总装阶段只插入基座。
    /// </summary>
    internal interface IFaceToFaceBaseAssembler
    {
        FaceToFaceBaseAssemblyToken InsertAtRoot(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            string rootCoordinateName);

        FaceToFaceBaseAssemblyToken InsertBaseOnElevated(
            string positionName,
            string modelPath,
            FaceToFaceBaseAssemblyToken elevatedBase);

        /// <summary>
        /// 将「模型对照映射」JSON 写入总装配体自定义属性并重建保存。
        /// </summary>
        void WriteModelMapping(string mappingJson);

        void SaveAndRebuild();

        void Rollback(
            IReadOnlyList<FaceToFaceBaseAssemblyToken> instancesInReverseOrder);
    }
}
