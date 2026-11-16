using Newtonsoft.Json;
using System.Collections.Generic;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 装配体自定义属性「图号生成信息」JSON 一次解析得到的只读快照。
    /// </summary>
    public sealed class DrawingNumberMetadataSnapshot
    {
        public DrawingNumberMetadataSnapshot(
            string contractNumber,
            string drawingNumber,
            string designer,
            string proofreader,
            string reviewer,
            string approver)
        {
            ContractNumber = contractNumber ?? string.Empty;
            DrawingNumber = drawingNumber ?? string.Empty;
            Designer = designer ?? string.Empty;
            Proofreader = proofreader ?? string.Empty;
            Reviewer = reviewer ?? string.Empty;
            Approver = approver ?? string.Empty;
        }

        /// <summary>合同号。</summary>
        public string ContractNumber { get; }

        /// <summary>图号。</summary>
        public string DrawingNumber { get; }

        /// <summary>设计。</summary>
        public string Designer { get; }

        /// <summary>校对。</summary>
        public string Proofreader { get; }

        /// <summary>审核。</summary>
        public string Reviewer { get; }

        /// <summary>批准。</summary>
        public string Approver { get; }

        /// <summary>
        /// 保留人员信息，只替换待分配图号。
        /// </summary>
        public DrawingNumberMetadataSnapshot WithDrawingNumber(string drawingNumber)
        {
            return new DrawingNumberMetadataSnapshot(
                ContractNumber,
                drawingNumber,
                Designer,
                Proofreader,
                Reviewer,
                Approver);
        }

        /// <summary>
        /// 序列化为总装配体「图号生成信息」JSON，字段名与图号生成窗口提交契约一致。
        /// </summary>
        public string ToJson()
        {
            var payload = new Dictionary<string, string>
            {
                ["合同号"] = ContractNumber ?? string.Empty,
                ["图号"] = DrawingNumber ?? string.Empty,
                ["设计"] = Designer ?? string.Empty,
                ["校对"] = Proofreader ?? string.Empty,
                ["审核"] = Reviewer ?? string.Empty,
                ["批准"] = Approver ?? string.Empty
            };
            return JsonConvert.SerializeObject(payload);
        }

        /// <summary>
        /// 将「图号生成信息」JSON 解析为快照（单次反序列化后填入全部字段）。
        /// </summary>
        public static bool TryParse(string json, out DrawingNumberMetadataSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (dict == null)
                {
                    return false;
                }

                snapshot = new DrawingNumberMetadataSnapshot(
                    Field(dict, "合同号"),
                    Field(dict, "图号"),
                    Field(dict, "设计"),
                    Field(dict, "校对"),
                    Field(dict, "审核"),
                    Field(dict, "批准"));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Field(Dictionary<string, string> dict, string key)
        {
            if (!dict.TryGetValue(key, out var raw) || raw == null)
            {
                return string.Empty;
            }

            return raw.Trim();
        }
    }
}
