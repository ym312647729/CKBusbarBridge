namespace CKBusbarBridge.models
{
    /// <summary>
    /// 铜排清单导出行数据（按图号聚合后）。
    /// </summary>
    public class BusbarBomItem
    {
        /// <summary>序号（写入 Excel 时赋值）。</summary>
        public int Index { get; set; }

        /// <summary>铜排名称（零件模型文件名，不含扩展名）。</summary>
        public string BusbarName { get; set; }

        /// <summary>图号（聚合键）。</summary>
        public string DrawingNumber { get; set; }

        /// <summary>同图号实例数量。</summary>
        public int Quantity { get; set; }

        /// <summary>铜排规格（如 80*8）。</summary>
        public string Specification { get; set; }

        /// <summary>零件类型。</summary>
        public string PartType { get; set; }

        /// <summary>材质名称。</summary>
        public string MaterialName { get; set; }

        /// <summary>单件净重（kg）。</summary>
        public double? NetWeightKg { get; set; }

        /// <summary>展开长（mm）。</summary>
        public string UnfoldedLength { get; set; }

        /// <summary>展开宽（mm，来自规格解析）。</summary>
        public double? UnfoldedWidth { get; set; }

        /// <summary>厚度（mm，来自规格解析）。</summary>
        public double? Thickness { get; set; }

        /// <summary>备注。</summary>
        public string Remarks { get; set; }
    }
}
