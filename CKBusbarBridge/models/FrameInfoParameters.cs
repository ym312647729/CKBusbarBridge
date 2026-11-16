using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 基架信息参数。
    /// </summary>
    public class FrameInfoParameters
    {
        /// <summary>
        /// 标准基架长度。
        /// </summary>
        public double StandardFrameLength { get; set; } = 1000;

        /// <summary>
        /// 基架数量。
        /// </summary>
        public int FrameCount { get; set; }

        /// <summary>
        /// 基架详细列表。
        /// </summary>
        public List<FrameDetail> Frames { get; set; }

        /// <summary>
        /// 基架信息参数。
        /// </summary>
        public FrameInfoParameters()
        {
            Frames = new List<FrameDetail>();
        }

        /// <summary>
        /// 简单校验：返回不合法字段的错误信息。
        /// </summary>
        public IEnumerable<string> Validate()
        {
            if (StandardFrameLength < 0) yield return "StandardFrameLength must be >= 0.";
            if (FrameCount < 0) yield return "FrameCount must be >= 0.";

            if (Frames == null)
            {
                yield return "Frames must not be null.";
                yield break;
            }

            for (var i = 0; i < Frames.Count; i++)
            {
                var frame = Frames[i];
                if (frame == null)
                {
                    yield return "Frames[" + i + "] must not be null.";
                    continue;
                }

                foreach (var err in frame.Validate())
                {
                    yield return "Frames[" + i + "]: " + err;
                }
            }
        }

        /// <summary>
        /// 基架详细信息。
        /// </summary>
        public class FrameDetail
        {
            /// <summary>
            /// 基架详细信息。
            /// </summary>
            public FrameDetail()
            {
                InsulatorPositions = new List<double>();
            }

            /// <summary>
            /// 基架长度。
            /// </summary>
            public double Length { get; set; }

            /// <summary>
            /// 基架宽度；绝缘子支架长度 = 该基架宽 − 20。
            /// </summary>
            public double Width { get; set; }

            /// <summary>
            /// 是否为大基架（面对墙场景 5GX.355.004）。
            /// 大基架内绝缘子相间距取套管相间距，定位不做墙柜偏心补偿。
            /// </summary>
            public bool UseLargeFrameTemplate { get; set; }

            /// <summary>
            /// 基架绝缘子定位列表。
            /// </summary>
            public List<double> InsulatorPositions { get; set; }

            /// <summary>
            /// 简单校验：返回不合法字段的错误信息。
            /// </summary>
            public IEnumerable<string> Validate()
            {
                if (Length < 0) yield return "Length must be >= 0.";

                if (InsulatorPositions == null)
                {
                    yield return "InsulatorPositions must not be null.";
                    yield break;
                }

                for (var i = 0; i < InsulatorPositions.Count; i++)
                {
                    if (InsulatorPositions[i] < 0) yield return "InsulatorPositions[" + i + "] must be >= 0.";
                }
            }
        }
    }
}
