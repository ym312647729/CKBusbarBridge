using System.Collections.Generic;
using GeometRi;

namespace CKBusbarBridgePreview
{
    public class BaseFrameData
    {
        /// <summary>
        /// 基架中心点坐标
        /// </summary>
        public Point3d CenterPoint { get; set; }

        /// <summary>
        /// 基架长度
        /// </summary>
        public double Length { get; set; }

        /// <summary>
        /// 基架宽度
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// 基架高度
        /// </summary>
        public double Height { get; set; } = 500;

        /// <summary>
        /// 是否反向安装
        /// </summary>
        public bool bReverse { get; set; }

        /// <summary>
        /// 基架X轴方向向量
        /// </summary>
        public Vector3d XVector3d { get; set; }

        /// <summary>
        /// 基架Y轴方向向量
        /// </summary>
        public Vector3d YVector3d { get; set; }

        #region 绝缘子相关参数
        /// <summary>
        /// 绝缘子安装数量
        /// </summary>
        public int InsulatorNum { get; set; } = 0;

        /// <summary>
        /// 绝缘子左侧起始距离
        /// </summary>
        public double InsulatorLeftDic { get; set; } = 50.0;

        /// <summary>
        /// 该基架内绝缘子相间距（mm）；未设置时预览回退到 BusDuctData.PhaseDis。
        /// 大基架取套管相间距，常规基架取伸出排相间距。
        /// </summary>
        public double InsulatorPhaseSpacing { get; set; }

        /// <summary>
        /// 绝缘子间距集合
        /// </summary>
        public List<double> InsulatorDic { get; set; } = new List<double>();
        #endregion

        /// <summary>
        /// 深拷贝方法，创建独立的新对象
        /// </summary>
        /// <returns>拷贝完成的 BaseFrameData 实例</returns>
        public BaseFrameData Copy()
        {
            return new BaseFrameData
            {
                // 值类型直接赋值
                Length = this.Length,
                Width = this.Width,
                Height = this.Height,
                bReverse = this.bReverse,
                InsulatorNum = this.InsulatorNum,
                InsulatorLeftDic = this.InsulatorLeftDic,
                InsulatorPhaseSpacing = this.InsulatorPhaseSpacing,
                InsulatorDic = new List<double>(this.InsulatorDic),

                // 几何对象使用自带 Copy 方法深拷贝
                CenterPoint = this.CenterPoint?.Copy(),
                XVector3d = this.XVector3d?.Copy(),
                YVector3d = this.YVector3d?.Copy()
            };
        }
    }
}
