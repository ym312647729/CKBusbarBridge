using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 铜排加工类型枚举（中文命名）
    /// </summary>
    public enum 面对墙类型
    {
        左立弯,
        右立弯,
        左偏移,
        右偏移,
        左中偏移,
        右中偏移,
        左铣排,
        右铣排
    }

    public class BusDuctData
    {
        public BusDuctData() { }

        #region 母排基础参数
        public Vector3d XVector { get; set; }
        public Vector3d YVector { get; set; }
        
        /// <summary>
        /// 相序数量
        /// </summary>
        public int PhaseNum { get; set; } = 3;

        /// <summary>
        /// 相序列表
        /// </summary>
        public List<BusbarPhase_e> AllBusbarPhase_e { get; set; } = new List<BusbarPhase_e>();

        /// <summary>
        /// 母排总数量
        /// </summary>
        public int BusbarNum { get; set; } = 4;

        /// <summary>
        /// 相间距（相邻相母排中心距）
        /// </summary>
        public double PhaseDis { get; set; } = 210.0;

        ///// <summary>
        ///// 母排起始位置（距基座左侧）
        ///// </summary>
        //public double StartPositionLeft { get; set; }

        /// <summary>
        /// 母排首段起始位置
        /// </summary>
        public double StartSegmentation { get; set; }

        /// <summary>
        /// 母排末段结束位置
        /// </summary>
        public double EndSegmentation { get; set; }

        /// <summary>
        /// 母排折弯预览配置
        /// </summary>
        public BendPreviewSetting BendSetting { get; set; } = new BendPreviewSetting();

        /// <summary>
        /// 额外分段位置列表
        /// </summary>
        public List<double> OtherPositions { get; set; } = new List<double>();
        #endregion

        #region B柜母排折弯/排布尺寸

        /// <summary>
        /// 母排起始点
        /// </summary>
        public Point3d BusbarStartPoint_B { get; set; }

        /// <summary>
        /// B柜-母排第一节高度
        /// </summary>
        public double BusbarH_Section1_B { get; set; } = 300.0;

        /// <summary>
        /// B柜-母排第二节高度
        /// </summary>
        public double BusbarH_Section2_B { get; set; } = 200.0;

        /// <summary>
        /// B柜-母排第三节高度
        /// </summary>
        public double BusbarH_Section3_B { get; set; } = 200.0;

        /// <summary>
        /// B柜-L排长度
        /// </summary>
        public double BusbarTotalLength_B { get; set; } = 300.0;

        /// <summary>
        /// L中心位置距
        /// </summary>
        public double BusbarCenterDis_B { get; set; } = 28.0;

        /// <summary>
        /// B柜-L排折弯距
        /// </summary>
        public double BusbarBendDis_B { get; set; } = 300.0;
        #endregion

        #region A柜母排折弯/排布尺寸

        /// <summary>
        /// A母排距B母排距离
        /// </summary>
        public double A_BDis { get; set; }

        /// <summary>
        /// A柜-母排第一节高度
        /// </summary>
        public double BusbarH_Section1_A { get; set; } = 300.0;

        /// <summary>
        /// A柜-母排第二节高度
        /// </summary>
        public double BusbarH_Section2_A { get; set; } = 200.0;

        /// <summary>
        /// A柜-母排第三节高度
        /// </summary>
        public double BusbarH_Section3_A { get; set; } = 200.0;

        /// <summary>
        /// A柜-L排长度
        /// </summary>
        public double BusbarTotalLength_A { get; set; } = 300.0;

        /// <summary>
        /// A_L中心位置距
        /// </summary>
        public double BusbarCenterDis_A { get; set; } = 28.0;

        /// <summary>
        /// A柜-L排折弯距
        /// </summary>
        public double BusbarBendDis_A { get; set; } = 300.0;
        #endregion

        #region 基架组参数
        /// <summary>
        /// 基架数据列表
        /// </summary>
        public List<BaseFrameData> BasesFrames { get; set; }
        #endregion

        #region 换相参数

        /// <summary>
        /// 换相位置
        /// </summary>
        public int IntChangePhase { get; set; } = -1;

        public double A_Height1 { get; set; } = 200.0;

        public double A_Length1 { get; set; } = 300.0;

        public double A_Length2 { get; set; } = 350.0;

        public double A_Length3 { get; set; } = 1000.0;

        public double A_Length4 { get; set; } = 500.0;

        public double B_Height1 { get; set; } = 80.0;

        public double B_Length1 { get; set; } = 800.0;

        public double C_Length1 { get; set; } = 1300.0;
        #endregion

        #region 立弯参数

        /// <summary>
        /// 立弯位置
        /// </summary>
        public int IntVBend { get; set; } = -1;

        /// <summary>
        /// 偏移距离（负为反向偏移）
        /// </summary>
        public double VBendOffset { get; set; } = 0;

        /// <summary>
        /// 非折弯段距离（平弯段距离，需减去一个排宽）
        /// </summary>
        public double BendLength{ get; set; } = 0;

        /// <summary>
        /// 立弯折弯半径
        /// </summary>
        public double VBendRadius { get; set; } = 0;

        #endregion

        #region 面对墙参数
        // 以下字段由 FrameDriverWindowVM.TryApplyFaceToWallPreviewData 写入，
        // 供 BusbarPreview 在 i >= IntFaceWall 时绘制末段立弯/偏移/铣排几何。

        /// <summary>
        /// 是否面对墙
        /// </summary>
        public bool IsFaceWall { get; set; }

        /// <summary>
        /// 面对墙特殊几何起始段索引（0 基，通常为末段 segmentLengths.Count - 1）。
        /// </summary>
        public int IntFaceWall { get; set; } = -1;

        /// <summary>
        /// 面对墙末段 A/B/C 逐相预览数据，按相序索引 0/1/2 对应 A/B/C。
        /// </summary>
        public List<FaceToWallPhasePreviewData> FaceToWallPhasePreviews { get; set; }
            = new List<FaceToWallPhasePreviewData>();

        /// <summary>套管相间距（mm），AC 相入墙 Y 向展开时使用。</summary>
        public double 套管相间距 { get; set; } = 500.0;

        /// <summary>
        /// 是否换相（仅对与面对墙）
        /// </summary>
        public bool IsChangePhase { get; set; }

        public 面对墙类型 Type面对墙类型 { get; set; }

        /// <summary>AC 相第一节直排长度（末段 L/4 + 搭接）。</summary>
        public double AC相第一节直排长度 { get; set; }

        /// <summary>AC 相折弯排第一段长度（末段 L/4）。</summary>
        public double AC相折弯排第一段长度 { get; set; }

        /// <summary>三相折弯排第二段长度（末段 L/2）。</summary>
        public double 三相折弯排第二段长度 { get; set; }

        /// <summary>三相折弯高度。</summary>
        public double 三相折弯高度 { get; set; }

        /// <summary>B 相偏移/立弯/铣排第一段长度。</summary>
        public double B相偏移第一段长度尺寸 { get; set; }

        /// <summary>B 相立弯第二段长度（仅立弯模板）。</summary>
        public double B相偏移第二段长度尺寸 { get; set; }

        /// <summary>偏移尺寸（|墙柜偏心值|）。</summary>
        public double 偏移尺寸 { get; set; }

        /// <summary>B 相铣排两段间距（铣排模板取排宽）。</summary>
        public double B相铣排两段铣排距离尺寸 { get; set; }

        #endregion

        /// <summary>
        /// 深拷贝当前对象（完整复制，新对象修改不影响原对象）
        /// </summary>
        /// <returns>复制后的新 BusDuctData 实例</returns>
        public BusDuctData Copy()
        {
            BusDuctData copy = new BusDuctData();

            // 1. 值类型直接复制
            copy.PhaseNum = this.PhaseNum;
            copy.BusbarNum = this.BusbarNum;
            copy.PhaseDis = this.PhaseDis;
            //copy.StartPositionLeft = this.StartPositionLeft;
            copy.StartSegmentation = this.StartSegmentation;
            copy.EndSegmentation = this.EndSegmentation;

            // 2. 引用类型：新建列表并复制元素
            copy.AllBusbarPhase_e = new List<BusbarPhase_e>(this.AllBusbarPhase_e);
            copy.OtherPositions = this.OtherPositions != null
                ? new List<double>(this.OtherPositions)
                : new List<double>();

            // 3. B柜母排参数复制
            copy.BusbarStartPoint_B = this.BusbarStartPoint_B?.Copy();
            copy.BusbarH_Section1_B = this.BusbarH_Section1_B;
            //copy.BusbarH_Section2_B = this.BusbarH_Section2_B;
            copy.BusbarH_Section3_B = this.BusbarH_Section3_B;
            copy.BusbarTotalLength_B = this.BusbarTotalLength_B;
            copy.BusbarCenterDis_B = this.BusbarCenterDis_B;
            copy.BusbarBendDis_B = this.BusbarBendDis_B;

            // 4. A柜母排参数复制
            copy.A_BDis = this.A_BDis;
            copy.BusbarH_Section1_A = this.BusbarH_Section1_A;
            //copy.BusbarH_Section2_A = this.BusbarH_Section2_A;
            copy.BusbarH_Section3_A = this.BusbarH_Section3_A;
            copy.BusbarTotalLength_A = this.BusbarTotalLength_A;
            copy.BusbarCenterDis_A = this.BusbarCenterDis_A;
            copy.BusbarBendDis_A = this.BusbarBendDis_A;

            // 5. 子对象深拷贝
            copy.BendSetting = this.BendSetting?.Copy();

            // 6. 基架列表深度拷贝
            copy.BasesFrames = new List<BaseFrameData>();
            if (this.BasesFrames != null)
            {
                foreach (BaseFrameData frame in this.BasesFrames)
                {
                    copy.BasesFrames.Add(frame?.Copy());
                }
            }

            // 7. 向量类型深拷贝
            copy.XVector = this.XVector?.Copy() ?? null;
            copy.YVector = this.YVector?.Copy() ?? null;

            // 8. 换相参数复制
            copy.IntChangePhase = this.IntChangePhase;
            copy.A_Height1 = this.A_Height1;
            copy.A_Length1 = this.A_Length1;
            copy.A_Length2 = this.A_Length2;
            copy.A_Length3 = this.A_Length3;
            copy.A_Length4 = this.A_Length4;
            copy.B_Height1 = this.B_Height1;
            copy.B_Length1 = this.B_Length1;
            copy.C_Length1 = this.C_Length1;

            return copy;
        }
    }
}
