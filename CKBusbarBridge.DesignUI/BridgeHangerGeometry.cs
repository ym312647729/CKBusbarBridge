using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using Newtonsoft.Json;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeHangerBinding
    {
        public string HangerId { get; set; }
        public double? LocalOffset { get; set; }
        [JsonIgnore]
        public string OwnerId => HangerId ?? FrameId;
        public string FrameId { get; set; }
        public string FrameInstance { get; set; }
        public string Instance { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string Mate { get; set; }
        public double Width { get; set; }
        public double AngleLength { get; set; }
        public double InstallationHeight { get; set; }
    }

    public interface IBridgeHangerSynchronizationOperations
    {
        bool Exists(BridgeHangerBinding binding);
        BridgeHangerBinding Create(BridgeHangerPlan plan);
        BridgeHangerBinding Update(BridgeHangerBinding binding, BridgeHangerPlan plan);
        void Remove(BridgeHangerBinding binding);
    }

    public sealed class BridgeHangerSynchronizer
    {
        public Dictionary<string, BridgeHangerBinding> Synchronize(IReadOnlyList<BridgeHangerPlan> plans,
            IReadOnlyDictionary<string, BridgeHangerBinding> previous, IBridgeHangerSynchronizationOperations operations)
        {
            if (plans.Select(p => p.Pose.HangerId).Distinct(StringComparer.Ordinal).Count() != plans.Count)
                throw new InvalidOperationException("吊架标识重复。");
            var candidate = new Dictionary<string, BridgeHangerBinding>(StringComparer.Ordinal);
            foreach (var plan in plans)
            {
                previous.TryGetValue(plan.Pose.HangerId, out var binding);
                var next = binding != null && operations.Exists(binding) ? operations.Update(binding, plan) : operations.Create(plan);
                if (next?.OwnerId != plan.Pose.HangerId || next.FrameId != plan.Pose.FrameId)
                    throw new InvalidOperationException("吊架同步返回了错误的标识或基架归属。");
                candidate.Add(next.OwnerId, next);
            }
            foreach (var old in previous.Where(p => !candidate.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                operations.Remove(old.Value);
            return candidate;
        }
    }

    public sealed class BridgeHangerModelGroup
    {
        public IReadOnlyList<BridgeHangerPlan> Plans { get; }
        public BridgeHangerBinding ExistingModel { get; }
        internal BridgeHangerModelGroup(IEnumerable<BridgeHangerPlan> plans, BridgeHangerBinding existingModel)
        { Plans = Array.AsReadOnly(plans.ToArray()); ExistingModel = existingModel; }
    }

    public sealed class BridgeHangerModelReusePlanner
    {
        private const double ModelTolerance = .000001;
        public IReadOnlyList<BridgeHangerModelGroup> Group(IReadOnlyList<BridgeHangerPlan> plans, IEnumerable<BridgeHangerBinding> existing)
        {
            var groups = new List<List<BridgeHangerPlan>>();
            foreach (var plan in plans)
            {
                var group = groups.FirstOrDefault(g => Same(g[0].Pose.Width, g[0].AngleLength, plan.Pose.Width, plan.AngleLength));
                if (group == null) { group = new List<BridgeHangerPlan>(); groups.Add(group); }
                group.Add(plan);
            }
            var models = existing.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Configuration, StringComparer.Ordinal).ThenBy(m => m.FrameId, StringComparer.Ordinal).ToArray();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return groups.Select(g =>
            {
                var model = models.FirstOrDefault(m => !used.Contains(m.Path) && g.All(p => Same(m.Width, m.AngleLength, p.Pose.Width, p.AngleLength)));
                if (model != null) used.Add(model.Path);
                return new BridgeHangerModelGroup(g, model);
            }).ToArray();
        }
        private bool Same(double widthA, double angleA, double widthB, double angleB) =>
            Math.Abs(widthA - widthB) <= ModelTolerance && Math.Abs(angleA - angleB) <= ModelTolerance;
    }

    public sealed class BridgeHangerRoofFace
    {
        public double[] Point { get; }
        public double[] Normal { get; }
        public BridgeHangerRoofFace(double[] point, double[] normal)
        {
            Point = HangerVectors.Copy(point); Normal = HangerVectors.Unit(normal);
        }
    }

    public sealed class BridgeHangerRoof
    {
        public string Identity { get; }
        public IReadOnlyList<BridgeHangerRoofFace> Faces { get; }
        public BridgeHangerRoof(string identity, IEnumerable<BridgeHangerRoofFace> faces)
        {
            Identity = identity;
            Faces = Array.AsReadOnly(faces.ToArray());
            if (string.IsNullOrWhiteSpace(identity) || Faces.Count == 0)
                throw new InvalidOperationException("横梁缺少唯一身份或可读取的实际平面。");
        }
    }

    public sealed class BridgeHangerPose
    {
        public string FrameId { get; }
        public string HangerId { get; }
        public double? LocalOffset { get; }
        public double Width { get; }
        public double[] Origin { get; }
        public double[] Along { get; }
        public double[] Up { get; }
        public double[] Across { get; }
        public BridgeHangerPose(string frameId, double width, double[] origin, double[] along, double[] up, double[] across)
            : this(frameId, width, origin, along, up, across, frameId, null) { }

        private BridgeHangerPose(string frameId, double width, double[] origin, double[] along, double[] up, double[] across,
            string hangerId, double? localOffset)
        {
            FrameId = frameId; Width = width; Origin = HangerVectors.Copy(origin);
            HangerId = hangerId; LocalOffset = localOffset;
            Along = HangerVectors.Unit(along); Up = HangerVectors.Unit(up); Across = HangerVectors.Unit(across);
            if (string.IsNullOrWhiteSpace(frameId) || !HangerVectors.Finite(width) || width <= 0 ||
                Math.Abs(HangerVectors.Dot(Along, Up)) > 1e-6 || Math.Abs(HangerVectors.Dot(Along, Across)) > 1e-6 ||
                Math.Abs(HangerVectors.Dot(Up, Across)) > 1e-6)
                throw new InvalidOperationException("吊杆缺少有效的基架实例、宽度或安装坐标系。");
        }
        public BridgeHangerPose At(string hangerId, double offset) =>
            new BridgeHangerPose(FrameId, Width, Point(offset, 0, 0), Along, Up, Across, hangerId, offset);
        public double[] Point(double x, double y, double z) => Enumerable.Range(0, 3)
            .Select(i => Origin[i] + x * Along[i] + y * Up[i] + z * Across[i]).ToArray();
    }

    public sealed class BridgeHangerPlan
    {
        public BridgeHangerPose Pose { get; }
        public double InstallationHeight { get; }
        public double AngleLength => InstallationHeight - BridgeHangerGeometry.FixedHeight;
        public BridgeHangerPlan(BridgeHangerPose pose, double installationHeight)
        {
            Pose = pose ?? throw new ArgumentNullException(nameof(pose));
            InstallationHeight = installationHeight;
            if (!HangerVectors.Finite(installationHeight) || installationHeight <= BridgeHangerGeometry.FixedHeight)
                throw new InvalidOperationException("基架“" + pose.FrameId + "”到横梁的空间不足，反算角钢长度必须大于零。");
        }
    }

    // 只含已实测模板关系和纯几何；生成宿主与高频预览共用未加密实现。
    public sealed class BridgeHangerGeometry
    {
        public const string CoordinateName = "吊杆配合坐标系";
        public const double RodLength = 1000.0;
        private const double RodBottom = -88.0;
        private const double RodTop = RodBottom + RodLength;
        // 新模板的距离1、距离2均为450.0 mm，通丝与角钢沿安装轴搭接500.0 mm。
        private const double AngleBottom = RodTop - 500.0;
        private const double TopInstallationOffset = 10.0;
        public const double FixedHeight = AngleBottom + TopInstallationOffset;
        public const double Tolerance = 1.0;
        public const double LowerHalfLength = 40.0;
        public const double UpperHalfLength = 80.0;
        public bool RequiresHanger(string templateKey) => templateKey != "LargeOffsetCorner";

        public BridgeHangerPlan Plan(BridgeHangerPose pose, BridgeHangerRoof roof)
        {
            if (roof == null) throw new InvalidOperationException("未读取总装中的横梁下表面。");
            // 横梁仅示意屋顶；安装面与其下表面的无限延伸水平面共面即可。
            return new BridgeHangerPlan(pose, Height(roof, pose.Origin, pose.Up));
        }

        private double Height(BridgeHangerRoof roof, double[] origin, double[] up)
        {
            var hits = new List<double>();
            foreach (var face in roof.Faces)
            {
                var alignment = HangerVectors.Dot(face.Normal, up);
                if (face.Normal.Zip(up, (a,b) => Math.Abs(a+b)).Any(v => v > 1e-6)) continue;
                var distance = HangerVectors.Dot(HangerVectors.Sub(face.Point, origin), face.Normal) / alignment;
                hits.Add(distance);
            }
            if (hits.Count == 0)
                throw new InvalidOperationException("横梁缺少与吊杆安装方向垂直的下表面水平面。");
            // 同一示意模型含多层朝下平面时，最低下表面定义屋顶标高。
            return hits.Min();
        }

        public BridgeHangerPose DraftPose(BridgeDesignSample sample, BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (!RequiresHanger(frame.FrameTemplateKey)) throw new InvalidOperationException("大偏移拐角基架不安装吊杆。");
            new BridgeHangerTemplatePose().Local(frame.FrameTemplateKey, frame.DriveValue(DesignField.Width), frame.DriveValue(DesignField.Height));
            var binding = frame.ComponentBinding;
            if (binding?.Transform != null && binding.DrivenValues != null)
            {
                var old = binding.DrivenValues;
                if (!new[] { DesignField.Width, DesignField.Height, DesignField.X, DesignField.Y, DesignField.Z, DesignField.Direction }.All(old.ContainsKey))
                    throw new InvalidOperationException("反读基架缺少原始驱动位置，无法定位吊杆：" + frame.Name);
                var native = new BridgeHangerTemplatePose().Local(frame.FrameTemplateKey, old[DesignField.Width], old[DesignField.Height]);
                var actual = FromTransform(frame.Id, frame.DriveValue(DesignField.Width), native, binding.Transform);
                var boundCoordinates = sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value);
                var previousOrigin = boundCoordinates.ToDraft(actual.Origin);
                var oldDirection = old[DesignField.Direction] + (old.TryGetValue(DesignField.Installation, out var reversed) && reversed > .5 ? 180 : 0);
                var boundAngle = (frame.ModelDirection - oldDirection) * Math.PI / 180;
                Func<double[], double[]> rotate = p => new[] { p[0]*Math.Cos(boundAngle)-p[1]*Math.Sin(boundAngle),
                    p[0]*Math.Sin(boundAngle)+p[1]*Math.Cos(boundAngle), p[2] };
                var relative = rotate(new[] { previousOrigin[0]-old[DesignField.X], previousOrigin[1]-old[DesignField.Y],
                    previousOrigin[2]-old[DesignField.Z] });
                var boundOrigin = boundCoordinates.ToAssembly(new[] { frame.DriveValue(DesignField.X)+relative[0], frame.DriveValue(DesignField.Y)+relative[1],
                    frame.DriveValue(DesignField.Z)+relative[2]-(frame.DriveValue(DesignField.Height)-old[DesignField.Height])/2 });
                Func<double[], double[]> direction = p => boundCoordinates.ToAssemblyDirection(rotate(boundCoordinates.ToDraftDirection(p)));
                return new BridgeHangerPose(frame.Id, frame.DriveValue(DesignField.Width), boundOrigin, direction(actual.Along), direction(actual.Up), direction(actual.Across));
            }
            var coordinates = sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value);
            var center = new BusbarBridgeDraftPreviewSceneFactory().WorldPosition(frame, objects);
            var angle = frame.ModelDirection * Math.PI / 180;
            var origin = coordinates.ToAssembly(new[] { frame.ParentId == null ? frame.DriveValue(DesignField.X) : center[0],
                frame.ParentId == null ? frame.DriveValue(DesignField.Y) : center[1],
                (frame.ParentId == null ? frame.DriveValue(DesignField.Z) : center[2]) - frame.DriveValue(DesignField.Height) / 2 });
            return new BridgeHangerPose(frame.Id, frame.DriveValue(DesignField.Width), origin,
                coordinates.ToAssemblyDirection(new[] { Math.Cos(angle), Math.Sin(angle), 0d }),
                coordinates.Up.ToArray(), coordinates.ToAssemblyDirection(new[] { -Math.Sin(angle), Math.Cos(angle), 0d }));
        }

        public BridgeHangerPose FromTransform(string id, double width, double[] local, double[] component)
        {
            var o = HangerVectors.Transform(new[] { local[9] * 1000, local[10] * 1000, local[11] * 1000 }, component, true);
            return new BridgeHangerPose(id, width, o,
                HangerVectors.Transform(local.Take(3).ToArray(), component, false),
                HangerVectors.Transform(local.Skip(3).Take(3).ToArray(), component, false),
                HangerVectors.Transform(local.Skip(6).Take(3).ToArray(), component, false));
        }

        public IReadOnlyList<DraftSupportSolid> Preview(BridgeHangerPlan plan)
        {
            var result = new List<DraftSupportSolid>(); var pose = plan.Pose; var index = 0;
            Action<double, double, double, double, double, double> plate = (x, y, z, sx, sy, sz) =>
            {
                var points = new[] { new[] { x,y,z },new[] { x+sx,y,z },new[] { x+sx,y+sy,z },new[] { x,y+sy,z },
                    new[] { x,y,z+sz },new[] { x+sx,y,z+sz },new[] { x+sx,y+sy,z+sz },new[] { x,y+sy,z+sz } };
                result.Add(new DraftSupportSolid(pose.HangerId + "/hanger/plate/" + index++, "rim", points.Select(p => pose.Point(p[0], p[1], p[2]))));
            };
            var half = (pose.Width + 150) / 2;
            foreach (var z in new[] { -half, half })
            {
                result.Add(new DraftSupportSolid(pose.HangerId + "/hanger/rod/" + index++, "rim",
                    new DraftRoundSection(pose.Point(0, RodBottom, z), pose.Point(0, RodTop, z), 8, 8)));
                plate(-25, AngleBottom, z - 27.5, 50, plan.AngleLength, 5);
                plate(-25, AngleBottom, z - 22.5, 5, plan.AngleLength, 45);
                plate(-UpperHalfLength, plan.InstallationHeight - TopInstallationOffset, z - 125, 2 * UpperHalfLength, TopInstallationOffset, 200);
            }
            var span = pose.Width + 200;
            plate(-LowerHalfLength, -5, -span / 2, 2 * LowerHalfLength, 5, span);
            plate(-LowerHalfLength, -43, -span / 2, 5, 38, span);
            plate(LowerHalfLength - 5, -43, -span / 2, 5, 38, span);
            return result.AsReadOnly();
        }
    }

    public sealed class BridgeHangerTemplatePose
    {
        public double[] Local(string key, double width, double height)
        {
            // 2026-10-24读取024/026/027/032为原点；029的截面转向由其专用坐标系定义。
            if (key == "SideToSidePhaseChange029")
                return new[] { 1d,0,0, 0,0,-1, 0,1,0, 0,width/2000,height/2000, 1,0,0,0 };
            if (key != "Regular" && key != "SmallOffset" && key != "FaceToWallLarge1" && key != "FaceToWallLarge2")
                throw new InvalidOperationException("未核验的基架吊杆安装坐标系模板：" + key);
            return new[] { 1d,0,0, 0,1,0, 0,0,1, 0,0,0, 1,0,0,0 };
        }
    }

    internal static class HangerVectors
    {
        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        internal static double[] Copy(double[] p)
        {
            if (p == null || p.Length != 3 || p.Any(v => !Finite(v))) throw new InvalidOperationException("吊杆几何必须提供有限的三维坐标。");
            return p.ToArray();
        }
        internal static double Dot(double[] a, double[] b) => a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
        internal static double[] Sub(double[] a, double[] b) => new[] {a[0]-b[0],a[1]-b[1],a[2]-b[2]};
        internal static double[] Unit(double[] p)
        {
            var v=Copy(p); var length=Math.Sqrt(Dot(v,v));
            if (length<1e-9) throw new InvalidOperationException("吊杆方向不能为零向量。");
            return v.Select(x=>x/length).ToArray();
        }
        internal static double[] Transform(double[] p, double[] t, bool point)
        {
            if (t == null || t.Length != 16 || t.Any(v => !Finite(v)) || Math.Abs(t[12]-1)>1e-8)
                throw new InvalidOperationException("吊杆组件缺少有效的无缩放安装变换。");
            return Enumerable.Range(0,3).Select(i=>p[0]*t[i]+p[1]*t[3+i]+p[2]*t[6+i]+(point?t[9+i]*1000:0)).ToArray();
        }
    }
}
