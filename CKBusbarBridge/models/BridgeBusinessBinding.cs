using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    // 实例身份与同源模型身份分开：同文件、同配置可以安装在多个位置。
    public sealed class BridgeComponentBinding
    {
        public bool GeometryChanged { get; set; }
        public string Instance { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string Inlet { get; set; }
        public string Outlet { get; set; }
        public double[] Transform { get; set; }
        public double[][] SourcePath { get; set; }
        public Dictionary<DesignField, double> DrivenValues { get; set; }
        public Dictionary<DesignField, double> DisplayValues { get; set; }
        public double[] InsulatorPositions { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public double[] InsulatorTransverseOffsets { get; set; }
        public string DocumentKey => (Path ?? string.Empty).ToUpperInvariant() + "|" + Configuration;

        internal BridgeComponentBinding WithPath(string path)
        {
            var copy = (BridgeComponentBinding)MemberwiseClone();
            copy.Path = path;
            return copy;
        }

        internal BridgeComponentBinding WithoutFrameInstallation()
        {
            if (DrivenValues?.ContainsKey(DesignField.Installation) != true &&
                DisplayValues?.ContainsKey(DesignField.Installation) != true) return this;
            // 保留实例、引用配置及实际接口；清理旧字段不能修改共享的历史绑定。
            var copy = (BridgeComponentBinding)MemberwiseClone();
            copy.DrivenValues = DrivenValues?.Where(p => p.Key != DesignField.Installation).ToDictionary(p => p.Key, p => p.Value);
            copy.DisplayValues = DisplayValues?.Where(p => p.Key != DesignField.Installation).ToDictionary(p => p.Key, p => p.Value);
            return copy;
        }
    }

    public sealed class BridgeReadbackBaseline
    {
        public string Signature { get; set; }
        public string Recipe { get; set; }
        public string Material { get; set; }
        // 实物基线不变；重开时根据待同步记录/上游指纹恢复这两个未提交目标状态。
        public bool RequiresCabinetSynchronization { get; set; }
        public bool RequiresWallCopperReplan { get; set; }
        // 实物签名/Recipe始终指向写入前状态，修复预览不能冒充已经完成的模型。
        public bool RequiresBridgeRepair { get; set; }
        public bool RepairPlanned { get; set; }
        public string RepairUpstreamFingerprint { get; set; }
        public double[] RepairStart { get; set; }
        public double[] RepairEnd { get; set; }
        public string[] RepairProblems { get; set; } = Array.Empty<string>();
        public int CopperCount { get; set; }
        public string[] SuppressedInstances { get; set; } = Array.Empty<string>();
    }
}
