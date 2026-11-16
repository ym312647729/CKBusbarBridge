using System;
using System.Collections.Generic;

namespace RD.WebPreview.Contracts
{
    /// <summary>通用 Web 三维预览 Worker 的协议常量。</summary>
    public static class WebPreviewWorkerProtocol
    {
        public const int CurrentVersion = 1;
        public const int CurrentSceneSchemaVersion = 1;

        public const string Handshake = "Handshake";
        public const string HandshakeAcknowledged = "HandshakeAcknowledged";
        public const string GenerateRequest = "GenerateRequest";
        public const string GenerateProgress = "GenerateProgress";
        public const string Generated = "Generated";
        public const string CancelRequest = "CancelRequest";
        public const string CancelAcknowledged = "CancelAcknowledged";
        public const string Cancelled = "Cancelled";
        public const string Shutdown = "Shutdown";
        public const string ShutdownAcknowledged = "ShutdownAcknowledged";
        public const string Faulted = "Faulted";

        public const string InlinePayload = "Inline";
        public const string FilePayload = "PayloadFile";
        public const string SelfContainedHtml = "SelfContainedHtml";
    }

    /// <summary>场景对象类别使用稳定字符串，方便非 .NET 调用方直接构造 Json。</summary>
    public static class WebPreviewObjectKinds
    {
        public const string Busbar = "Busbar";
        public const string Component = "Component";
        public const string SupportBlock = "SupportBlock";
        public const string Accessory = "Accessory";
        public const string Mesh = "Mesh";
        public const string BoxFallback = "BoxFallback";
        public const string ReferenceSurface = "ReferenceSurface";
        public const string Polyline = "Polyline";
    }

    /// <summary>stdin/stdout 单行 Json 消息信封。</summary>
    public sealed class WebPreviewWorkerMessageDto
    {
        public int ProtocolVersion { get; set; } = WebPreviewWorkerProtocol.CurrentVersion;
        public string MessageType { get; set; }
        public string RequestId { get; set; }
        public int ParentProcessId { get; set; }
        public string AuthorizationCode { get; set; }
        public WebPreviewGenerateRequestDto GenerateRequest { get; set; }
        public WebPreviewGenerateResultDto GenerateResult { get; set; }
        public WebPreviewGenerateProgressDto Progress { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorDetails { get; set; }
    }

    public sealed class WebPreviewGenerateRequestDto
    {
        public string PayloadMode { get; set; } = WebPreviewWorkerProtocol.InlinePayload;
        public WebPreviewSceneDto Scene { get; set; }
        public string PayloadFilePath { get; set; }
        public long PayloadFileLength { get; set; }
        public string PayloadFileSha256 { get; set; }
        public string OutputMode { get; set; } = WebPreviewWorkerProtocol.SelfContainedHtml;
        public int ExpirationHours { get; set; } = 24;
    }

    public sealed class WebPreviewGenerateResultDto
    {
        public string PreviewId { get; set; }
        public string EntryHtmlPath { get; set; }
        public string PreviewDirectory { get; set; }
        public string SceneHash { get; set; }
        public string GeneratedAtUtc { get; set; }
        public string ExpiresAtUtc { get; set; }
        public long HtmlFileLength { get; set; }
        public int ObjectCount { get; set; }
        public int BusbarCount { get; set; }
        public int ComponentCount { get; set; }
        public long TriangleCount { get; set; }
        public bool IsCacheHit { get; set; }
        public IList<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class WebPreviewGenerateProgressDto
    {
        public string Stage { get; set; }
        public int Percent { get; set; }
        public string Message { get; set; }
    }

    /// <summary>所有宿主统一使用毫米和右手坐标系的纯场景数据。</summary>
    public sealed class WebPreviewSceneDto
    {
        public int SceneSchemaVersion { get; set; } =
            WebPreviewWorkerProtocol.CurrentSceneSchemaVersion;
        public string SceneId { get; set; }
        public string Title { get; set; }
        public string LengthUnit { get; set; } = "Millimeter";
        public string BackgroundColor { get; set; } = "#F7F8FA";
        public bool ShowCenterLines { get; set; }
        public IList<WebPreviewObjectDto> Objects { get; set; } =
            new List<WebPreviewObjectDto>();
        public IDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }

    public sealed class WebPreviewObjectDto
    {
        public string ObjectId { get; set; }
        public string GroupId { get; set; }
        public string ParentObjectId { get; set; }
        public string DisplayName { get; set; }
        public string ObjectKind { get; set; }
        public string BusinessCategory { get; set; }
        public bool Visible { get; set; } = true;
        public bool Selectable { get; set; } = true;
        public WebPreviewColorDto Color { get; set; }
        public double Opacity { get; set; } = 1d;
        public WebPreviewBusbarDto Busbar { get; set; }
        public WebPreviewComponentDto Component { get; set; }
        public WebPreviewTriangleMeshDto Mesh { get; set; }
        public WebPreviewBoxDto Box { get; set; }
        public WebPreviewPolylineDto Polyline { get; set; }
    }

    public sealed class WebPreviewColorDto
    {
        public int Red { get; set; }
        public int Green { get; set; }
        public int Blue { get; set; }
        public int Alpha { get; set; } = 255;

        public WebPreviewColorDto()
        {
        }

        public WebPreviewColorDto(int red, int green, int blue, int alpha = 255)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }
    }

    public sealed class WebPreviewCoordinateDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public WebPreviewCoordinateDto()
        {
        }

        public WebPreviewCoordinateDto(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// 一根铜排的语义几何。PathPoints 可表达中心线或厚度一侧基准线，
    /// 由 IsCenterLine 明确区分，不能让渲染端猜测。
    /// </summary>
    public sealed class WebPreviewBusbarDto
    {
        public bool IsCenterLine { get; set; }
        public IList<WebPreviewCoordinateDto> PathPoints { get; set; } =
            new List<WebPreviewCoordinateDto>();
        public IList<WebPreviewCoordinateDto> CenterLinePoints { get; set; } =
            new List<WebPreviewCoordinateDto>();
        public WebPreviewCoordinateDto ThicknessDirection { get; set; }
        public WebPreviewCoordinateDto WidthDirection { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public double BendRadius { get; set; }
        public double StartLapWidth { get; set; }
        public double EndLapWidth { get; set; }
        public string Phase { get; set; }
    }

    /// <summary>普通元器件由多个 Body/Face 网格构成，不携带任何 CAD COM 对象。</summary>
    public sealed class WebPreviewComponentDto
    {
        public IList<WebPreviewBodyDto> Bodies { get; set; } =
            new List<WebPreviewBodyDto>();
        public WebPreviewBoxDto FallbackBox { get; set; }
    }

    public sealed class WebPreviewBodyDto
    {
        public string BodyId { get; set; }
        public IList<WebPreviewFaceDto> Faces { get; set; } =
            new List<WebPreviewFaceDto>();
    }

    public sealed class WebPreviewFaceDto
    {
        public string FaceId { get; set; }
        public string SurfaceType { get; set; }
        public bool IsPlane { get; set; }
        public WebPreviewColorDto Color { get; set; }
        public WebPreviewTriangleMeshDto Triangles { get; set; }
        public IList<WebPreviewPolylineDto> Edges { get; set; } =
            new List<WebPreviewPolylineDto>();
    }

    public sealed class WebPreviewTriangleMeshDto
    {
        public double[] Vertices { get; set; }
        public int[] Indices { get; set; }
        public double[] Normals { get; set; }
        public WebPreviewColorDto Color { get; set; }
    }

    public sealed class WebPreviewBoxDto
    {
        public WebPreviewCoordinateDto Minimum { get; set; }
        public WebPreviewCoordinateDto Maximum { get; set; }
    }

    public sealed class WebPreviewPolylineDto
    {
        public IList<WebPreviewCoordinateDto> Points { get; set; } =
            new List<WebPreviewCoordinateDto>();
        public WebPreviewColorDto Color { get; set; }
        public double LineWidth { get; set; } = 1d;
        public bool Dashed { get; set; }
    }
}
