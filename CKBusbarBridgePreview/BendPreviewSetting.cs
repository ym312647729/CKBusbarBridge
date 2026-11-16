using GeometRi;
using SolidWorks.Interop.sldworks;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 母线折弯预览配置类（核心参数封装）
    /// 【功能】封装母线折弯预览所需的所有几何参数、业务规则、路径数据、校验逻辑，是折弯预览的核心数据载体
    /// 【参数分类】必填参数（无默认值/必须输入）、可选参数（有默认值）、预览自动获取参数（无需输入）
    /// </summary>
    public class BendPreviewSetting
    {
        #region 新增：Copy方法（仅复制基本参数）

        /// <summary>
        /// 复制当前实例的基本参数（核心必填+关键业务参数，排除自动生成/暂存类参数）
        /// </summary>
        /// <returns>包含基本参数的新BendPreviewSetting实例</returns>
        public BendPreviewSetting Copy()
        {
            return new BendPreviewSetting
            {
                // 1. 折弯类型与核心方向（仅复制必填/核心参数，自动获取参数不复制）
                IntStyle = this.IntStyle,
                StartPoint = this.StartPoint?.Copy(), // Point3d是引用类型，需调用Copy避免浅拷贝
                StartBusbarVector = this.StartBusbarVector?.Copy(), // Vector3d同理
                WidthVector = this.WidthVector?.Copy(),

                // 2. 路径核心数据（仅复制当前路径，历史路径/临时点不复制）
                ListPoint = new List<string>(this.ListPoint), // 字符串列表深拷贝
                PointGather = this.PointGather?.Select(p => p.Copy()).ToList() ?? new List<Point3d>(), // Point3d列表深拷贝

                // 3. 折弯尺寸核心参数（必填数值型）
                BarBendingRadius = this.BarBendingRadius,
                BusBarVerticalRadius = this.BusBarVerticalRadius,
                BusBarThickness = this.BusBarThickness,
                BusBarWidth = this.BusBarWidth,

                // 4. 业务规则核心参数（有默认值/关键业务参数）
                K = this.K,
                FixedForm = this.FixedForm,
                PartTypeE = this.PartTypeE,
                Style = this.Style,
                Phase = this.Phase,
                IsMilledBar = this.IsMilledBar,
                MilledBarOriginalWidth = this.MilledBarOriginalWidth,
                MilledBarAdditionalWidth = this.MilledBarAdditionalWidth,
                MilledBarStartLength = this.MilledBarStartLength,
                MilledBarEndLength = this.MilledBarEndLength,
                MilledBarFeatureSide = this.MilledBarFeatureSide,

                // 排除的参数：自动获取参数、暂存参数、临时关联对象
                // VerticalThicknessVector3d、EndWidthVector、LastListPoint、LastCenterPoint
                // UseIndexGetComponent2AndFace2、EndAllowanceAddWide、styleList
            };
        }

        #endregion

        #region 折弯类型与核心方向参数

        /// <summary>
        /// 折弯类型标记（预览时自动获取）
        /// 1=仅平弯，2=平弯+立弯，3=平弯+立弯+平弯
        /// </summary>
        public int IntStyle { get; set; }

        /// <summary>
        /// 折弯起始点（单位自定义，必填）
        /// 作为折弯预览的基准起始坐标
        /// </summary>
        public Point3d StartPoint { get; set; }

        /// <summary>
        /// 起始钣金厚度方向向量（必填）
        /// 定义母线厚度方向，用于折弯厚度方向的几何计算
        /// </summary>
        public Vector3d StartBusbarVector { get; set; }

        /// <summary>
        /// 起始宽度方向向量（必填）
        /// 定义母线宽度方向，用于折弯宽度方向的几何计算
        /// </summary>
        public Vector3d WidthVector { get; set; }

        /// <summary>
        /// 立弯钣金厚度方向向量（预览自动获取，无需输入）
        /// 立弯场景下的厚度方向，由预览逻辑自动计算赋值
        /// </summary>
        public Vector3d VerticalThicknessVector3d { get; set; }

        /// <summary>
        /// 结尾宽度方向向量（预览自动获取，无需输入）
        /// 折弯结束段的宽度方向，由预览逻辑自动计算赋值
        /// </summary>
        public Vector3d EndWidthVector { get; set; }

        #endregion

        #region 路径与点位数据

        /// <summary>
        /// 铜排路径点集合（必填，单位：mm）
        /// 格式："X,Y,Z,平/立" → 坐标+折弯类型（平弯/立弯）
        /// 存储折弯路径的所有关键点及对应折弯类型
        /// </summary>
        public List<string> ListPoint { get; set; } = new List<string>();

        /// <summary>
        /// 历史路径点集合（暂存，无需输入）
        /// 存储历史路径数据，用于撤销/恢复操作
        /// </summary>
        public List<List<string>> LastListPoint { get; set; } = new List<List<string>>();

        /// <summary>
        /// 结尾中心定位点（圆心，预览自动获取）
        /// 折弯结束段的圆弧中心点（如有圆弧折弯时使用）
        /// </summary>
        public string LastCenterPoint { get; set; }

        /// <summary>
        /// 路径点三维坐标集合（由ListPoint转换而来）
        /// 用于几何计算的结构化点位数据
        /// </summary>
        public List<Point3d> PointGather { get; set; } = new List<Point3d>();

        #endregion

        #region 折弯尺寸参数（必填，单位：mm）

        /// <summary>
        /// 平弯折弯半径（必填）
        /// 平弯场景下的折弯圆弧半径
        /// </summary>
        public double BarBendingRadius { get; set; }

        /// <summary>
        /// 立弯折弯半径（必填）
        /// 立弯场景下的折弯圆弧半径
        /// </summary>
        public double BusBarVerticalRadius { get; set; }

        /// <summary>
        /// 母线板厚（必填）
        /// 母线钣金的厚度值，用于折弯余量计算
        /// </summary>
        public double BusBarThickness { get; set; }

        /// <summary>
        /// 母线排宽（必填）
        /// 母线的宽度值，用于宽度方向的几何计算
        /// </summary>
        public double BusBarWidth { get; set; }

        ///// <summary>
        ///// 起始搭接长度
        ///// </summary>
        //public double StartLapLength { get; set; }

        /// <summary>
        /// 起始搭接宽度
        /// </summary>
        public double StartLapWidth { get; set; }


        ///// <summary>
        ///// 末端搭接长度
        ///// </summary>
        //public double EndLapLength { get; set; }

        /// <summary>
        /// 末端搭接长度
        /// </summary>
        public double EndLapWidth { get; set; }

        #endregion

        #region 关联对象与业务规则参数

        /// <summary>
        /// 对应序号的搭接面组件+面对象列表（预览自动获取，无需输入）
        /// Tuple结构：(序号, 组件对象, 面对象) → 关联折弯段与对应的配合面
        /// </summary>
        public List<Tuple<int, Component2, Face2>> UseIndexGetComponent2AndFace2 { get; set; } =
            new List<Tuple<int, Component2, Face2>>();

        /// <summary>
        /// K因子（非必填，默认0.47）
        /// 折弯展开计算的核心系数，影响折弯余量的精度
        /// </summary>
        public double K { get; set; } = 0.47;

        /// <summary>
        /// 目标端配合面宽度（暂存）
        /// 临时存储目标端配合面的宽度值，用于动态调整
        /// </summary>
        public double EndAllowanceAddWide { get; set; }

        /// <summary>
        /// 固定形式（默认1）
        /// 标记母线的固定方式，1=默认固定规则
        /// </summary>
        public int FixedForm { get; set; } = 1;

        /// <summary>
        /// 当前预览实体对应的零件类型。
        /// </summary>
        public PartType_e PartTypeE { get; set; } = PartType_e.Busbar;

        /// <summary>
        /// 当前普通直排预览段属于铣排组合体。
        /// </summary>
        public bool IsMilledBar { get; set; }

        /// <summary>铣排两端保留的原始排宽，单位为毫米。</summary>
        public double MilledBarOriginalWidth { get; set; }

        /// <summary>铣排毛坯相对原始排宽增加的宽度，单位为毫米。</summary>
        public double MilledBarAdditionalWidth { get; set; }

        /// <summary>从铜排路径起点到铣削加宽段起点沿模板局部排长 X 方向的距离，单位为毫米。</summary>
        public double MilledBarStartLength { get; set; }

        /// <summary>从铣削加宽段终点到铜排路径末端沿模板局部排长 X 方向的距离，单位为毫米。</summary>
        public double MilledBarEndLength { get; set; }

        /// <summary>正式模板需要启用的左/右铣排特征。</summary>
        public MilledBarFeatureSide_e MilledBarFeatureSide { get; set; }

        /// <summary>
        /// 铜排形状（如L/M/G/G2/N/N_V/U等）
        /// 对应不同的折弯形状规则
        /// </summary>
        public string Style { get; set; }

        /// <summary>
        /// 母线相序（可选）
        /// 标记母线的相位（A/B/C相），用于相位匹配校验
        /// </summary>
        public BusbarPhase_e? Phase { get; set; }

        #endregion

        #region 核心方法：数据转换与校验

        /// <summary>
        /// 路径点转换：将ListPoint（字符串格式）转为PointGather（三维坐标集合），并执行合规性校验
        /// </summary>
        /// <returns>转换成功且校验通过返回true，否则返回false</returns>
        public bool GetPointGather()
        {
            // 核心转换逻辑：拆分字符串坐标，转为Point3d对象
            PointGather = ListPoint.Select(x => new Point3d(
                Convert.ToDouble(x.Split(',')[0]), // X坐标
                Convert.ToDouble(x.Split(',')[1]), // Y坐标
                Convert.ToDouble(x.Split(',')[2]))).ToList(); // Z坐标

            return true;
        }

        /// <summary>
        /// 路径点反向转换：将PointGather（三维坐标）转回ListPoint（字符串格式），并清理无效点位
        /// </summary>
        /// <param name="record">是否记录历史路径（默认true）</param>
        public void SetPointGather(bool record = true)
        {
            // 场景1：路径点为空 → 清空字符串路径列表
            if (PointGather == null || PointGather.Count < 1)
            {
                ListPoint = new List<string>();
            }
            // 场景2：路径点有效 → 清理无效点并转换格式
            else
            {
                // 记录历史路径（用于撤销/恢复）
                if (record)
                {
                    LastListPoint.Add(ListPoint);
                }

                // 第一步：清理重复点位（两点距离<0.001视为同一位置）
                for (int i = 0; i < PointGather.Count - 1; i++)
                {
                    if (PointGather[i].DistanceTo(PointGather[i + 1]) < 0.001)
                    {
                        PointGather.RemoveAt(i + 1);
                        i--; // 移除后索引回退，避免漏检
                    }
                }

                // 第二步：清理共线点位（点到线段距离<0.001视为共线，移除中间点）
                for (int i = 0; i < PointGather.Count - 2; i++)
                {
                    if (PointGather[i].DistanceTo(new Line3d(PointGather[i + 1], PointGather[i + 2])) < 0.001)
                    {
                        PointGather.Remove(PointGather[i + 1]);
                        i--; // 移除后索引回退
                    }
                }

                // 第三步：更新起始点为第一个路径点
                StartPoint = PointGather[0];

                // 第四步：转换为字符串格式（默认标记为"平"弯）
                ListPoint = PointGather.Select(p => $"{p.X},{p.Y},{p.Z},平").ToList();
            }
        }

        /// <summary>
        /// 预览合规性校验（核心校验逻辑）
        /// 【规则】仅当IfCheckConformsPreview=true时执行，校验路径点的角度合规性
        /// </summary>
        /// <returns>校验通过返回true，否则返回false</returns>
        public void CheckConformsPreview()
        {
            var sharpBendSegmentNumbers = new List<int>();

            // 场景2：开启校验 → 执行角度合规性校验
            if (WidthVector != null)
            {
                // 子场景2.1：路径点数量<3 → 无需角度校验，直接通过
                if (PointGather.Count < 3)
                {
                    return;
                }

                // 子场景2.2：路径点数量≥3 → 校验连续三点的夹角（≤90°视为合规）
                for (int i = 0; i < PointGather.Count - 2; i++)
                {
                    // 计算连续两段路径的向量
                    var v1 = new Vector3d(PointGather[i + 1], PointGather[i]);
                    var v2 = new Vector3d(PointGather[i + 1], PointGather[i + 2]);

                    // 计算向量夹角（角度制）
                    var d = v1.Normalized.AngleToDeg(v2.Normalized);


                    if (d < 89.95)
                    {
                        sharpBendSegmentNumbers.Add(i + 1);
                    }
                }

                if (sharpBendSegmentNumbers.Count > 0)
                {
                    FaceToWallPreviewTrace.Warning(
                        "铜排折弯预览-角度校验",
                        "以下折弯段出现锐角，请手动检查：" +
                        string.Join(",", sharpBendSegmentNumbers));
                }
            }
        }

        #endregion

        public List<string> styleList { get; set; } = new List<string>();
    }
}
