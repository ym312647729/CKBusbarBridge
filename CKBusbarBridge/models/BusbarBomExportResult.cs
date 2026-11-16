using System.Collections.Generic;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 铜排清单导出结果。
    /// </summary>
    public class BusbarBomExportResult
    {
        /// <summary>输出 Excel 完整路径。</summary>
        public string OutputPath { get; set; }

        /// <summary>导出的铜排种类数（聚合后行数）。</summary>
        public int RowCount { get; set; }

        /// <summary>遍历到的铜排实例总数（聚合前）。</summary>
        public int InstanceCount { get; set; }

        /// <summary>警告信息（缺属性、无材质、属性冲突等）。</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
