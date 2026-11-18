using System.Collections.Generic;

namespace RouteLinkBar.Algorithm.Contracts
{
    /// <summary>
    /// 方案库匹配输入。只包含组级纯几何数据，不包含数据库实体、Json 文本或 SolidWorks 对象。
    /// </summary>
    public sealed class LinkageSchemeLibraryMatchRequest
    {
        /// <summary>Worker 只读加载匹配库的绝对 .db 路径；有值时不需宿主发送候选快照。</summary>
        public string DatabasePath { get; set; }
        public LinkageSchemeLibraryProfile Target { get; set; }
        public IList<LinkageSchemeLibraryProfile> Candidates { get; set; } =
            new List<LinkageSchemeLibraryProfile>();
        public LinkageSchemeLibraryMatchOptions Options { get; set; } =
            new LinkageSchemeLibraryMatchOptions();
    }

    public sealed class LinkageSchemeLibraryProfile
    {
        public int SchemeRecordId { get; set; }
        public string SchemeName { get; set; }
        /// <summary>
        /// Optional pure-data route template used by the Worker to rebuild the
        /// candidate with the current request's fixed ends. Matching-only
        /// callers may omit it for backward compatibility.
        /// </summary>
        public LinkageSchemeLibraryRouteTemplate RouteTemplate { get; set; }
        public IList<LinkageSchemeLibraryGroup> StartGroups { get; set; } =
            new List<LinkageSchemeLibraryGroup>();
        public IList<LinkageSchemeLibraryGroup> CabinetTopGroups { get; set; } =
            new List<LinkageSchemeLibraryGroup>();
    }

    public sealed class LinkageSchemeLibraryGroup
    {
        /// <summary>稳定组键，例如 G1。</summary>
        public string GroupKey { get; set; }
        public int AssociationGroupNumber { get; set; }
        public LinkageSchemeLibraryVector AnchorCenter { get; set; }
        public LinkageSchemeLibraryVector WidthDirection { get; set; }
        public LinkageSchemeLibraryVector ThicknessDirection { get; set; }
        public LinkageSchemeLibraryVector PathDirection { get; set; }
        public double OverallWidth { get; set; }
        public double OverallThickness { get; set; }
        public IList<LinkageSchemeLibraryMember> Members { get; set; } =
            new List<LinkageSchemeLibraryMember>();
    }

    public sealed class LinkageSchemeLibraryMember
    {
        public string PositionNumber { get; set; }
        public LinkageSchemeLibraryVector RelativeAnchor { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public LinkageSchemeLibraryVector WidthDirection { get; set; }
        public LinkageSchemeLibraryVector ThicknessDirection { get; set; }
        public LinkageSchemeLibraryVector PathDirection { get; set; }
    }

    public sealed class LinkageSchemeLibraryVector
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public LinkageSchemeLibraryVector Clone()
        {
            return new LinkageSchemeLibraryVector { X = X, Y = Y, Z = Z };
        }
    }

    public sealed class LinkageSchemeLibraryMatchOptions
    {
        public double StartGroupWidthToleranceMm { get; set; } = 100d;
        public double GroupPositionWeight { get; set; } = 0.65d;
        public double GroupDistanceMatrixWeight { get; set; } = 0.20d;
        public double StartGroupInternalLayoutWeight { get; set; } = 0.10d;
        public double GroupDirectionWeight { get; set; } = 0.05d;
        public bool AllowMirror { get; set; } = true;
        public int TopResultCount { get; set; } = 10;
        public double MinimumPositionScaleMm { get; set; } = 1d;
    }

    public sealed class LinkageSchemeLibraryMatchResult
    {
        /// <summary>Worker 从匹配库读取的端部组纯 Json，供界面显示组映射，无需界面回库。</summary>
        public string CandidateProfileJson { get; set; }
        public int CandidateSchemeRecordId { get; set; }
        public string CandidateSchemeName { get; set; }
        public double Score { get; set; }
        public double GroupPositionScore { get; set; }
        public double GroupDistanceMatrixScore { get; set; }
        public double StartGroupInternalLayoutScore { get; set; }
        public double GroupDirectionScore { get; set; }
        public double PositionRmsErrorMm { get; set; }
        public double MaximumPositionErrorMm { get; set; }
        public double MaximumStartGroupWidthErrorMm { get; set; }
        public bool IsMirrored { get; set; }
        public LinkageSchemeLibraryRigidTransform CandidateToTargetTransform { get; set; }
        /// <summary>
        /// Worker-rebuilt route solution. It is optional so the legacy
        /// SchemeLibraryMatchRequest remains a matching-only operation.
        /// </summary>
        public LinkageRouteSolution GeneratedRouteSolution { get; set; }
        public IList<LinkageSchemeLibraryGroupMapping> Mappings { get; set; } =
            new List<LinkageSchemeLibraryGroupMapping>();
    }

    /// <summary>
    /// Pure algorithm representation of a persisted customer path. It carries
    /// topology and physical section specifications without database or CAD
    /// types, and is intentionally optional at the protocol boundary.
    /// </summary>
    public sealed class LinkageSchemeLibraryRouteTemplate
    {
        public int SchemeRecordId { get; set; }
        public string SchemeName { get; set; }
        public IList<LinkageSchemeLibraryRouteTemplateRoute> Routes { get; set; } =
            new List<LinkageSchemeLibraryRouteTemplateRoute>();
    }

    public sealed class LinkageSchemeLibraryRouteTemplateRoute
    {
        public string RouteKey { get; set; }
        public string CandidateStartGroupKey { get; set; }
        public string CandidateCabinetTopGroupKey { get; set; }
        public IList<LinkageSchemeLibraryRouteTemplateMember> OrderedMembers { get; set; } =
            new List<LinkageSchemeLibraryRouteTemplateMember>();
        public IList<LinkageSchemeLibraryRouteTemplateInterface> OrderedInterfaces { get; set; } =
            new List<LinkageSchemeLibraryRouteTemplateInterface>();
    }

    public sealed class LinkageSchemeLibraryRouteTemplateMember
    {
        public string MemberKey { get; set; }
        public string CandidateGroupKey { get; set; }
        public int PositionType { get; set; }
        public int SourceBusbarIndex { get; set; }
        public string Shape { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public double BendRadius { get; set; }
        public LinkageSchemeLibraryVector StartThicknessDirection { get; set; }
        public LinkageSchemeLibraryVector WidthDirection { get; set; }
        public IList<LinkageSchemeLibraryVector> CenterlinePoints { get; set; } =
            new List<LinkageSchemeLibraryVector>();
    }

    public sealed class LinkageSchemeLibraryRouteTemplateInterface
    {
        public string FirstMemberKey { get; set; }
        public string SecondMemberKey { get; set; }
        public int FirstSegmentIndex { get; set; }
        public int SecondSegmentIndex { get; set; }
        public double OverlapWidth { get; set; }
        public double OverlapLength { get; set; }
        public double NormalDistance { get; set; }
        public LinkageSchemeLibraryVector Center { get; set; }
        public LinkageSchemeLibraryVector FirstFaceNormal { get; set; }
        public LinkageSchemeLibraryVector SecondFaceNormal { get; set; }
    }

    public sealed class LinkageSchemeLibraryGroupMapping
    {
        public string TargetGroupKey { get; set; }
        public string CandidateGroupKey { get; set; }
        /// <summary>0=始端，1=柜顶。</summary>
        public int PositionType { get; set; }
        public double PositionErrorMm { get; set; }
        public double WidthErrorMm { get; set; }
        public int TargetMemberCount { get; set; }
        public int CandidateMemberCount { get; set; }
    }

    public sealed class LinkageSchemeLibraryRigidTransform
    {
        public double M11 { get; set; }
        public double M12 { get; set; }
        public double M13 { get; set; }
        public double M21 { get; set; }
        public double M22 { get; set; }
        public double M23 { get; set; }
        public double M31 { get; set; }
        public double M32 { get; set; }
        public double M33 { get; set; }
        public double TranslationX { get; set; }
        public double TranslationY { get; set; }
        public double TranslationZ { get; set; }
        public bool IsMirrored { get; set; }

        public LinkageSchemeLibraryVector Transform(LinkageSchemeLibraryVector point)
        {
            if (point == null)
            {
                return null;
            }

            return new LinkageSchemeLibraryVector
            {
                X = M11 * point.X + M12 * point.Y + M13 * point.Z + TranslationX,
                Y = M21 * point.X + M22 * point.Y + M23 * point.Z + TranslationY,
                Z = M31 * point.X + M32 * point.Y + M33 * point.Z + TranslationZ
            };
        }

        public LinkageSchemeLibraryVector TransformVector(LinkageSchemeLibraryVector vector)
        {
            if (vector == null)
            {
                return null;
            }

            return new LinkageSchemeLibraryVector
            {
                X = M11 * vector.X + M12 * vector.Y + M13 * vector.Z,
                Y = M21 * vector.X + M22 * vector.Y + M23 * vector.Z,
                Z = M31 * vector.X + M32 * vector.Y + M33 * vector.Z
            };
        }
    }
}
