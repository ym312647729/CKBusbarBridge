using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.models
{
    public class BusbarGenerationPreparationResult
    {/// <summary>
     /// 当前活动总装配体的完整路径。
     /// </summary>
        public string ActiveAssemblyPath { get; set; }

        /// <summary>
        /// 当前设计场景，用于解析插件模板缓存中的铜排模板根路径。
        /// </summary>
        public DesignScenarioId DesignScenarioId { get; set; } = DesignScenarioId.FaceToFace;

        /// <summary>
        /// 项目根目录路径。
        /// </summary>
        public string ProjectPath { get; set; }

        /// <summary>
        /// 各段直排/桥型排的目标长度列表（不包含 L 排）。
        /// </summary>
        public List<double> SegmentLengths { get; set; } = new List<double>();

        /// <summary>
        /// 生成铜排前整理出的段排信息集合。
        /// </summary>
        public List<BusbarSegmentInfo> BusbarSegments { get; set; } = new List<BusbarSegmentInfo>();

        /// <summary>
        /// A 柜侧 L 排驱动数据。
        /// </summary>
        public LBusbarData ACabinetLBusbarData { get; set; }

        /// <summary>
        /// B 柜侧 L 排驱动数据。
        /// </summary>
        public LBusbarData BCabinetLBusbarData { get; set; }

        /// <summary>
        /// 母排宽度（mm）。
        /// </summary>
        public double BusbarWidth { get; set; }

        /// <summary>
        /// 母排厚度（mm）。
        /// </summary>
        public double BusbarThickness { get; set; }

        /// <summary>
        /// 单相根数。
        /// </summary>
        public int BusbarNum { get; set; }

        /// <summary>
        /// 相间距（mm）。
        /// </summary>
        public double PhaseSpacing { get; set; }

        /// <summary>
        /// 柜体驱动中记录的铜排规格原始文本（用于回显/排查）。
        /// </summary>
        public string BusbarSpec { get; set; }

        /// <summary>
        /// 柜体驱动中记录的铜排材质（直角铜母线 / 全圆边铜母线）。
        /// </summary>
        public string BusbarMaterial { get; set; }

        /// <summary>
        /// 当前界面输入的母排搭接长度（mm）。
        /// </summary>
        public double OverlapLength { get; set; }

        /// <summary>
        /// 面对墙末段特殊铜排当前断点相对最后基架连接点的纵向布局。
        /// </summary>
        public FaceToWallSpecialBusbarLongitudinalLayout FaceToWallSpecialBusbarLongitudinalLayout { get; set; }

        /// <summary>
        /// 面对墙场景墙柜偏心值（mm）；正为右偏，负为左偏。
        /// </summary>
        public double WallCabinetEccentricity { get; set; }

        /// <summary>
        /// 面对墙场景套管相间距（mm），对应立弯模板全局变量「墙内相间距」。
        /// </summary>
        public double WallBushingPhaseSpacing { get; set; }

        /// <summary>
        /// 面对墙场景套管相间距1（F2）。当前与 <see cref="WallBushingPhaseSpacing2"/> 相等。
        /// </summary>
        public double WallBushingPhaseSpacing1 { get; set; }

        /// <summary>
        /// 面对墙场景套管相间距2（F3）。当前与 <see cref="WallBushingPhaseSpacing1"/> 相等。
        /// </summary>
        public double WallBushingPhaseSpacing2 { get; set; }

        /// <summary>
        /// 面对墙场景入墙排中心距墙孔底距离（mm）。
        /// </summary>
        public double WallEntryBusbarCenterToHoleBottomDistance { get; set; }

        /// <summary>
        /// 绝缘子高度（mm）。
        /// </summary>
        public double InsulatorBodyHeight { get; set; }

        /// <summary>
        /// 面对墙末段 A/B/C 三个单相特殊铜排生成计划。
        /// </summary>
        public List<FaceToWallSpecialBusbarPhasePlan> FaceToWallSpecialBusbarPhasePlans { get; set; }
            = new List<FaceToWallSpecialBusbarPhasePlan>();
    }
}
