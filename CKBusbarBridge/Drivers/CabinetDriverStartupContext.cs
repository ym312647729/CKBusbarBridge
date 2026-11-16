using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge
{
    /// <summary>
    /// 柜体驱动窗口首次打开时使用的 SolidWorks 上下文。
    /// 保存已识别的总装配体，避免 ViewModel 再按项目路径重新打开同一模型。
    /// </summary>
    internal sealed class CabinetDriverStartupContext
    {
        /// <summary>
        /// 初始化一个已通过活动总装配体识别流程验证的启动上下文。
        /// </summary>
        /// <param name="assemblyModel">SolidWorks 当前活动的柜体总装配体。</param>
        /// <param name="scenarioId">从总装配体自定义属性中读取的设计场景。</param>
        /// <param name="projectPath">总装配体所在的项目根目录。</param>
        /// <param name="cabinetOrientation">面对墙项目独立属性中保存的柜体朝向；面对面项目为空。</param>
        internal CabinetDriverStartupContext(
            IModelDoc2 assemblyModel,
            DesignScenarioId scenarioId,
            string projectPath,
            FaceToWallCabinetOrientation? cabinetOrientation)
        {
            AssemblyModel = assemblyModel;
            ScenarioId = scenarioId;
            ProjectPath = projectPath;
            CabinetOrientation = cabinetOrientation;
        }

        /// <summary>
        /// 获取当前已打开的柜体总装配体。
        /// </summary>
        internal IModelDoc2 AssemblyModel { get; }

        /// <summary>
        /// 获取总装配体声明的设计场景。
        /// </summary>
        internal DesignScenarioId ScenarioId { get; }

        /// <summary>
        /// 获取需要反填到柜体驱动界面的项目根目录。
        /// </summary>
        internal string ProjectPath { get; }

        /// <summary>
        /// 获取面对墙总装配体中单独保存的柜体朝向。
        /// </summary>
        internal FaceToWallCabinetOrientation? CabinetOrientation { get; }
    }
}
