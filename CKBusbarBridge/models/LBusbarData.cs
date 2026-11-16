using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// L排尺寸信息
    /// </summary>
    public class LBusbarData
    {
        /// <summary>
        /// 母排第一节高度
        /// </summary>
        public double BusbarH_Section1 { get; set; }

        /// <summary>
        /// 母排第二节高度
        /// </summary>
        public double BusbarH_Section2 { get; set; }

        /// <summary>
        /// 母排第三节高度
        /// </summary>
        public double BusbarH_Section3 { get; set; }

        /// <summary>
        /// L排长度
        /// </summary>
        public double BusbarTotalLength { get; set; }

        /// <summary>
        /// L排折弯距
        /// </summary>
        public double BusbarBendDis { get; set; }
    }
}
