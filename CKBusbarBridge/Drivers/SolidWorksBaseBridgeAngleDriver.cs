using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using AngleModel = CKBusbarBridge.Drivers.SolidWorksFaceToFaceFrameModelOperations;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SolidWorksBaseBridgeAngleDriver
    {
        private const string BridgePort = "基座安装坐标系";
        private readonly ISldWorks _app;
        private readonly SolidWorksDriver _driver;
        private readonly string _template;
        private readonly (string Prefix, string Feature, string Dimension)[] _cuts;
        private readonly IReadOnlyList<double> _grades;

        internal static string IndependentOutputDirectory(string projectDirectory) =>
            Path.Combine(projectDirectory, "基座", "母线桥角钢");

        internal static bool IsOrdinaryBasePath(string path) => string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase) &&
            new[] { "020", "030", "031" }.Any(template => AngleModel.SmallOffsetSealPlateIdentifierMatches(path, "5GX.355." + template));

        internal SolidWorksBaseBridgeAngleDriver(ISldWorks app, string template)
        {
            _app = app;
            _driver = new SolidWorksDriver(app);
            _template = template;
            _grades = template == "030" ? BridgeAngleNotchPlan.FourDepthsMm : BridgeAngleNotchPlan.RegularDepthsMm;
            switch (template)
            {
                case "020":
                case "031":
                    _cuts = new[]
                    {
                        ("8GX.160.008", "切除-拉伸1", "D2@草图2"),
                        ("8GX.160.A08", "切除-拉伸1", "D2@草图2")
                    };
                    break;
                case "030":
                    _cuts = new[]
                    {
                        ("8GX.160.155", "切除-拉伸1", "D2@草图2"),
                        ("8GX.160.152", "切除-拉伸3", "D1@草图7")
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(template), "未核验普通基座桥端角钢模板。");
            }
        }

        internal sealed class MountedBase
        {
            internal string Position;
            internal IComponent2 Base;
            internal IComponent2 Owner;
            internal IComponent2[] Angles;
            internal double OpeningHeightMm;
            internal IEnumerable<IComponent2> References => new[] { Base, Owner }.Concat(Angles);
        }

        private string Label(MountedBase mounted, int side) => mounted.Position +
            (_template == "030" ? (side == 0 ? "上角钢" : "下角钢") : "桥口角钢") + " / " + mounted.Angles[side].Name2;

        internal MountedBase[] Locate(IModelDoc2 root, IReadOnlyDictionary<string, int> baseIds)
        {
            return baseIds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var component = ComponentById(root, pair.Value);
                var model = RequireModel(component);
                if (!MatchesPath(component, "5GX.355." + _template))
                    throw new InvalidOperationException("普通基座模板与场景不符：" + pair.Key + " / " + component.Name2);
                var owners = Children(component).Where(c => MatchesPath(c, "6GX.355." + _template)).ToArray();
                if (owners.Length != 1)
                    throw new InvalidOperationException("无法唯一定位桥端角钢的所属子装配体：" + component.Name2);
                var owner = owners[0];
                RequireModel(owner);
                var children = Children(owner);
                var port = model.Extension.GetCoordinateSystemTransformByName(BridgePort) as MathTransform;
                var inverse = component.Transform2?.Inverse() as MathTransform;
                var portData = port?.ArrayData as double[];
                if (inverse == null || portData == null || portData.Length < 12 || !FinitePosition(portData))
                    throw new InvalidOperationException("普通基座缺少有效的桥端接口变换：" + component.Name2);

                var openingHeight = 0d;
                if (!CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(model, "基座开孔高度", out openingHeight) ||
                    !(openingHeight > 0d) || double.IsInfinity(openingHeight))
                    throw new InvalidOperationException("普通基座缺少基座开孔高度原变量：" + component.Name2);

                var angles = _cuts.Select((cut, index) =>
                {
                    var matches = children.Where(c => MatchesPath(c, cut.Prefix)).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException("无法唯一定位基座桥端角钢：" + component.Name2 + " / " + cut.Prefix);
                    var angle = matches[0];
                    RequireModel(angle);
                    var local = (angle.Transform2?.Multiply(inverse) as MathTransform)?.ArrayData as double[];
                    var tolerance = BridgeDesignPrecision.LengthToleranceMm;
                    var sign = index == 0 ? 1d : -1d;
                    if (local == null || local.Length < 12 || !FinitePosition(local) ||
                        Math.Abs(local[9] - portData[9]) * 1000d > tolerance ||
                        (_template == "030"
                            ? Math.Abs((local[10] - portData[10]) * 1000d - sign * openingHeight / 2d) > tolerance
                            : sign * (local[11] - portData[11]) * 1000d <= tolerance))
                        throw new InvalidOperationException("角钢位置与已核验的基座桥端不符：" + angle.Name2);
                    return angle;
                }).ToArray();
                return new MountedBase { Position = pair.Key, Base = component, Owner = owner, Angles = angles, OpeningHeightMm = openingHeight };
            }).ToArray();
        }

        internal MountedBase[] Drive(IModelDoc2 root, IReadOnlyDictionary<string, int> baseIds, IReadOnlyList<Component2> copper,
            IDrawingNumberGenerator numbers, string outputDirectory, Func<string, bool> owns, Action<string, int, int> progress = null)
        {
            new SolidWorksParentDimensionDriver(_app, owns).Synchronize(root, baseIds.Values.Select(id => ComponentById(root, id)));
            var bases = Locate(root, baseIds);
            if (bases.Length == 0 || copper.Count == 0) throw new InvalidOperationException("基座角钢驱动缺少已装配基座或铜排。");
            var angleCount = bases.Sum(b => b.Angles.Length);
            // 每根角钢分别计入选档、驱动确认和实物复测。共用文件写入后确认其全部实例。
            var total = angleCount * 3;
            var completed = 0;
            progress?.Invoke("正在准备已装配铜排实体", completed, total);
            var copperBodies = copper.SelectMany(c => SolidBodies(c)
                .Select(body => (Name: c.Name2, Body: CopyBody(body, c.Transform2, c.Name2)))).ToArray();
            // 先从本次基座参数计算全部纵向目标；旧开关状态只用于比较和恢复。
            var angles = bases.SelectMany(b => b.Angles.Select((angle, side) =>
            {
                var current = ReadTarget(angle, side);
                return (Angle: angle, Side: side, Current: current,
                    Geometry: BaseBridgeAngleNotchTarget.ForOpening(_template, b.OpeningHeightMm, current.DepthMm),
                    Label: Label(b, side), Position: b.Position + " " + _cuts[side].Prefix);
            })).ToArray();
            var candidates = new Dictionary<(string Path, string Config, int Side, string Target), Body2[]>();
            BaseBridgeAngleClearancePlan.PlanAndApply(angles.Select(a => a.Geometry).ToArray(),
                angles.Select(a => a.Label).ToArray(), _grades, (index, target) =>
                {
                    var item = angles[index];
                    completed = index;
                    progress?.Invoke("正在校验 " + item.Position + " · " + DepthDisplay(target.ActiveDepthMm), completed, total);
                    var transform = item.Angle.Transform2;
                    Body2[] bodies;
                    if (item.Current.Matches(target))
                    {
                        bodies = SolidBodies(item.Angle);
                    }
                    else
                    {
                        var key = (item.Angle.GetPathName(), item.Angle.ReferencedConfiguration, item.Side, target.Key);
                        if (!candidates.TryGetValue(key, out bodies))
                        {
                            bodies = CandidateBodies(item.Angle, item.Side, target, owns);
                            candidates.Add(key, bodies);
                        }
                    }
                    var minimum = (Distance: double.PositiveInfinity, Copper: string.Empty);
                    foreach (var body in bodies)
                    {
                        var installed = CopyBody(body, transform, item.Label);
                        foreach (var bar in copperBodies)
                        {
                            var distance = Distance(root, installed, bar.Body, item.Label, bar.Name);
                            // 中间档发现任一处不足即可试下一档，最后一档保留真正的最近铜排用于报错。
                            if (target.ActiveDepthMm != BridgeAngleNotchPlan.MaximumDepthMm && !BaseBridgeAngleClearancePlan.MeetsClearance(distance))
                                return (distance, bar.Name);
                            if (distance < minimum.Distance) minimum = (distance, bar.Name);
                        }
                    }
                    return minimum;
                }, frozen =>
                {
                    completed = angleCount;
                    progress?.Invoke("全部角钢完整目标已冻结，正在核对共用关系", completed, total);
                    var targets = bases.Select((b, i) => b.Angles.Select((_, side) => frozen[i * 2 + side]).ToArray()).ToArray();
                    SWHelper.EnsureAssemblyEditingContext(_app, root);

                    // 在改文件前确认引用全部在本次事务范围内。
                    foreach (var reference in bases.SelectMany(b => b.References))
                        RequireOwned(owns, reference.GetPathName());
                    RequireNoOtherReferences(root, bases);
                    if (bases.Length == 2)
                        bases = SeparateConflictingReferences(root, baseIds, bases, targets, numbers, outputDirectory, owns,
                            detail => progress?.Invoke(detail, completed, total));

                    var writes = bases.SelectMany((b, i) => b.Angles.Select((angle, side) => (Angle: angle, Side: side, Target: targets[i][side])))
                        .GroupBy(target => target.Angle.GetPathName(), StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var group in writes)
                    {
                        var target = group.First();
                        if (group.Any(item => item.Side != target.Side || item.Angle.ReferencedConfiguration != target.Angle.ReferencedConfiguration ||
                            !item.Target.Matches(target.Target)))
                            throw new InvalidOperationException("共享基座角钢仍有不同豁口目标：" + group.Key);
                    }
                    if (_template == "030")
                    {
                        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var group in writes)
                        {
                            var target = group.First();
                            if (!ReadTarget(target.Angle, target.Side).Matches(target.Target)) changedPaths.Add(group.Key);
                        }
                        Preserve030InstallationAxes(bases, changedPaths, owns);
                    }
                    foreach (var group in writes)
                    {
                        var target = group.First();
                        var partName = Path.GetFileNameWithoutExtension(group.Key);
                        if (!ReadTarget(target.Angle, target.Side).Matches(target.Target))
                        {
                            progress?.Invoke("正在统一写入 " + partName + " · " + DepthDisplay(target.Target.ActiveDepthMm), completed, total);
                            var model = Open(group.Key);
                            var cut = _cuts[target.Side];
                            AngleModel.WriteBaseBridgeAngleNotch(model, cut.Feature, cut.Dimension, target.Target);
                            Rebuild(model);
                            RequireTarget(target.Angle, target.Side, target.Target);
                            AngleModel.SaveAngleModel(model);
                        }
                        completed += group.Count();
                        progress?.Invoke("已确认完整豁口 " + partName + " · " + DepthDisplay(target.Target.ActiveDepthMm), completed, total);
                    }

                    progress?.Invoke("正在重建总装并反读豁口", completed, total);
                    SWHelper.EnsureAssemblyEditingContext(_app, root);
                    Rebuild(root);
                    bases = Locate(root, baseIds);
                    for (var i = 0; i < bases.Length; i++)
                        for (var side = 0; side < 2; side++)
                            RequireTarget(bases[i].Angles[side], side, targets[i][side]);
                    VerifyClearance(root, bases, copper, report: true,
                        progress: (detail, verified) => progress?.Invoke(detail, completed + verified, total));
                });
            return bases;
        }

        private void Preserve030InstallationAxes(IReadOnlyList<MountedBase> bases, ISet<string> changedPaths, Func<string, bool> owns)
        {
            var parallel = Math.Cos(1.0 * Math.PI / 180.0);
            foreach (var mounted in bases.Where(b => b.Angles.Any(angle => changedPaths.Contains(angle.GetPathName())))
                .GroupBy(b => b.Base.GetPathName(), StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                var path = mounted.Base.GetPathName();
                var configuration = mounted.Base.ReferencedConfiguration;
                var instance = mounted.Base.Name2;
                RequireOwned(owns, path);
                var model = Open(path);
                AngleModel.RequireDefaultReference(model, configuration, instance);
                SWHelper.EnsureAssemblyEditingContext(_app, model);
                var assembly = (IAssemblyDoc)model;
                var expected = new Dictionary<string, double[]>();
                var stable = default((byte[] X, byte[] Z, bool FlipX, bool FlipZ, double[] Transform));
                for (var index = 1; index <= 3; index++)
                {
                    var name = "绝缘子安装坐标系" + index + "(A)";
                    var feature = assembly.IFeatureByName(name);
                    if (!(feature?.GetDefinition() is ICoordinateSystemFeatureData data)) continue;
                    var before = data.Transform?.ArrayData as double[];
                    if (!data.AccessSelections((ModelDoc2)model, null))
                        throw new InvalidOperationException("无法读取030基座安装坐标系的轴引用：" + path + " / " + name);
                    try
                    {
                        var axes = new[] { data.XAxisEntities, data.YAxisEntities, data.ZAxisEntities }
                            .OfType<object[]>().SelectMany(items => items).OfType<IEntity>();
                        if (!axes.Any(entity => changedPaths.Contains(entity.IGetComponent2()?.GetPathName() ?? string.Empty)))
                            continue;
                        if (!(data.OriginEntity is ISketchPoint) || data.GetYAxisEntitiesCount() != 0)
                            throw new InvalidOperationException("030基座安装坐标系引用与已核验模板不符：" + path + " / " + name);
                        FrameInsulatorPositionReadback.RequireTransform(before, name);
                        expected.Add(name, before);
                    }
                    finally { data.ReleaseSelectionAccess(); }

                    // A侧原点继续由原草图驱动，方向改用同一基座B侧已经核验的非驱动角钢边。
                    if (stable.Transform == null) stable = Read030StableInstallationAxes(model, assembly, owns);
                    var xAlignment = CoordinateAxisDot(before, stable.Transform, 0);
                    var zAlignment = CoordinateAxisDot(before, stable.Transform, 2);
                    if (Math.Abs(xAlignment) < parallel || Math.Abs(zAlignment) < parallel)
                        throw new InvalidOperationException("030基座稳定轴方向与原安装坐标系不符：" + path + " / " + name);

                    feature = assembly.IFeatureByName(name);
                    data = (ICoordinateSystemFeatureData)feature.GetDefinition();
                    if (!data.AccessSelections((ModelDoc2)model, null))
                        throw new InvalidOperationException("无法编辑030基座安装坐标系：" + path + " / " + name);
                    var modified = false;
                    try
                    {
                        var x = model.Extension.GetObjectByPersistReference3(stable.X, out var xStatus);
                        var z = model.Extension.GetObjectByPersistReference3(stable.Z, out var zStatus);
                        if (!(x is IEdge) || !(z is IEdge) || xStatus != 0 || zStatus != 0)
                            throw new InvalidOperationException("无法重新定位030基座稳定轴边：" + path + " / " + name);
                        data.XAxisEntities = new[] { new DispatchWrapper(x) };
                        data.ZAxisEntities = new[] { new DispatchWrapper(z) };
                        data.XFlipped = stable.FlipX ^ (xAlignment < 0.0);
                        data.ZFlipped = stable.FlipZ ^ (zAlignment < 0.0);
                        modified = feature.ModifyDefinition(data, model, null);
                        if (!modified)
                            throw new InvalidOperationException("030基座安装坐标系轴引用更新失败：" + path + " / " + name);
                    }
                    finally { if (!modified) data.ReleaseSelectionAccess(); }
                }

                if (expected.Count == 0) continue;
                Rebuild(model);
                foreach (var entry in expected)
                {
                    var feature = assembly.IFeatureByName(entry.Key);
                    var data = feature?.GetDefinition() as ICoordinateSystemFeatureData;
                    var actual = data?.Transform?.ArrayData as double[];
                    FrameInsulatorPositionReadback.RequireTransform(actual, entry.Key);
                    var positionDistance = BridgeBusinessGeometry.Distance(entry.Value.Skip(9).Take(3).ToArray(),
                        actual.Skip(9).Take(3).ToArray()) * 1000.0;
                    if (feature.GetErrorCode2(out _) != 0 || positionDistance > 1.0 ||
                        Enumerable.Range(0, 3).Any(axis => CoordinateAxisDot(entry.Value, actual, axis) < parallel))
                        throw new InvalidOperationException("030基座安装坐标系轴引用更新后位姿不符：" + path + " / " + entry.Key);
                    if (!data.AccessSelections((ModelDoc2)model, null))
                        throw new InvalidOperationException("无法复核030基座安装坐标系的轴引用：" + path + " / " + entry.Key);
                    try
                    {
                        Read030StableAxis(model, data.XAxisEntities, "8GX.160.151", owns);
                        Read030StableAxis(model, data.ZAxisEntities, "8GX.051.028", owns);
                    }
                    finally { data.ReleaseSelectionAccess(); }
                }
                AngleModel.SaveAngleModel(model);
                FaceToWallPreviewTrace.Info("030基座安装坐标系轴引用已核验", path + "；" + string.Join("、", expected.Keys));
            }
        }

        private static (byte[] X, byte[] Z, bool FlipX, bool FlipZ, double[] Transform) Read030StableInstallationAxes(
            IModelDoc2 model, IAssemblyDoc assembly, Func<string, bool> owns)
        {
            const string name = "绝缘子安装坐标系1(B)";
            var data = assembly.IFeatureByName(name)?.GetDefinition() as ICoordinateSystemFeatureData;
            var transform = data?.Transform?.ArrayData as double[];
            FrameInsulatorPositionReadback.RequireTransform(transform, name);
            if (!data.AccessSelections((ModelDoc2)model, null))
                throw new InvalidOperationException("无法读取030基座稳定轴引用：" + model.GetPathName());
            try
            {
                return (Read030StableAxis(model, data.XAxisEntities, "8GX.160.151", owns),
                    Read030StableAxis(model, data.ZAxisEntities, "8GX.051.028", owns),
                    data.XFlipped, data.ZFlipped, transform);
            }
            finally { data.ReleaseSelectionAccess(); }
        }

        private static byte[] Read030StableAxis(IModelDoc2 model, object entities, string prefix, Func<string, bool> owns)
        {
            var edge = (entities as object[])?.SingleOrDefault() as IEdge;
            var path = (edge as IEntity)?.IGetComponent2()?.GetPathName();
            if (string.IsNullOrWhiteSpace(path) || !AngleModel.SmallOffsetSealPlateIdentifierMatches(path, prefix))
                throw new InvalidOperationException("030基座稳定轴未引用已核验的零件边：" + model.GetPathName() + " / " + prefix);
            RequireOwned(owns, path);
            return model.Extension.GetPersistReference3(edge) as byte[]
                ?? throw new InvalidOperationException("030基座稳定轴缺少持久引用：" + path);
        }

        private static double CoordinateAxisDot(double[] first, double[] second, int axis) =>
            Enumerable.Range(axis * 3, 3).Sum(index => first[index] * second[index]);

        internal bool VerifyClearance(IModelDoc2 root, IReadOnlyList<MountedBase> bases, IReadOnlyList<Component2> copper,
            bool report = false, Action<string, int> progress = null)
        {
            if (bases.Count == 0 || copper.Count == 0) throw new InvalidOperationException("基座角钢复测缺少已装配基座或铜排。");
            var verified = 0;
            foreach (var b in bases)
                for (var side = 0; side < 2; side++)
                {
                    var angle = b.Angles[side];
                    var label = Label(b, side);
                    var position = b.Position + " " + _cuts[side].Prefix;
                    progress?.Invoke("正在复测 " + position, verified);
                    var minimum = (Distance: double.PositiveInfinity, Copper: string.Empty);
                    foreach (var bar in copper)
                    {
                        var distance = Distance(root, angle, bar, label, bar.Name2);
                        if (distance < minimum.Distance) minimum = (distance, bar.Name2);
                    }
                    if (!BaseBridgeAngleClearancePlan.MeetsClearance(minimum.Distance))
                    {
                        if (report) throw BaseBridgeAngleClearancePlan.ClearanceFailure(label, minimum, "装配复测");
                        return false;
                    }
                    if (report)
                        FaceToWallPreviewTrace.Info("基座桥端角钢复测", FormattableString.Invariant(
                            $"{label}，豁口{Depth(angle, side):0.0} mm，最近铜排{minimum.Copper}，绝缘距离{minimum.Distance:0.0} mm"));
                    verified++;
                    progress?.Invoke(FormattableString.Invariant($"已复测 {position} · {minimum.Distance:0.0} mm"), verified);
                }
            return true;
        }

        private static string DepthDisplay(double depth) => depth == 0d ? "无豁口" : FormattableString.Invariant($"{depth:0.0} mm");

        internal string Signature(IReadOnlyList<MountedBase> bases, Func<string, string> relativePath)
        {
            return new BridgeGenerationRecipeCodec().Hash(string.Join("\n", bases.Select(b => b.Position + "|" +
                string.Join("|", b.References.Select(c => c.GetID() + "/" + relativePath(c.GetPathName()) + "/" + c.ReferencedConfiguration)) +
                "|" + string.Join(",", b.Angles.Select((c, side) => CanonicalTarget(b, side).Key)))));
        }

        internal bool GeometryCurrent(IReadOnlyList<MountedBase> bases) => bases.All(b => b.Angles.Select((angle, side) =>
        {
            var current = ReadTarget(angle, side);
            return current.Matches(BaseBridgeAngleNotchTarget.ForOpening(_template, b.OpeningHeightMm, current.DepthMm)
                .WithDepth(current.ActiveDepthMm));
        }).All(matches => matches));

        private BaseBridgeAngleNotchTarget CanonicalTarget(MountedBase mounted, int side)
        {
            var actual = ReadTarget(mounted.Angles[side], side);
            var expected = BaseBridgeAngleNotchTarget.ForOpening(_template, mounted.OpeningHeightMm, actual.DepthMm)
                .WithDepth(actual.ActiveDepthMm);
            if (actual.Matches(expected)) actual = expected;
            var grade = _grades.Where(value => value > 0d && Math.Abs(value - actual.DepthMm) <= BridgeDesignPrecision.LengthToleranceMm)
                .Select(value => (double?)value).SingleOrDefault();
            return new BaseBridgeAngleNotchTarget(grade ?? actual.DepthMm, actual.Suppressed, actual.LengthMm, actual.EndOffsetMm);
        }

        private MountedBase[] SeparateConflictingReferences(IModelDoc2 root, IReadOnlyDictionary<string, int> ids,
            MountedBase[] bases, BaseBridgeAngleNotchTarget[][] targets, IDrawingNumberGenerator numbers, string output, Func<string, bool> owns,
            Action<string> progress)
        {
            var conflicts = targets[0].Zip(targets[1], (a, b) => !a.Matches(b)).ToArray();
            if (!conflicts.Any(conflict => conflict)) return bases;
            var retainedPaths = bases[0].References.Select(reference => reference.GetPathName()).ToArray();
            if (SharesFile(bases[0].Base, bases[1].Base))
            {
                progress("正在使 " + bases[1].Position + " 普通基座独立");
                MakeIndependent(root, root, bases[1].Base, "5GX.355." + _template, numbers, output, owns, bases[0].Base, saveOwner: false);
                bases = RebindAfterIndependence(root, ids);
            }
            if (SharesFile(bases[0].Owner, bases[1].Owner))
            {
                progress("正在使 " + bases[1].Position + " 角钢所属子装配体独立");
                MakeIndependent(root, RequireModel(bases[1].Base), bases[1].Owner, "6GX.355." + _template, numbers, output, owns,
                    preserveExistingMateErrors: true);
                bases = RebindAfterIndependence(root, ids);
            }
            var baseModel = RequireModel(bases[1].Base);
            var ownerId = bases[1].Owner.GetID();
            FrameAngleInstanceState ownerBefore = null;
            for (var side = 0; side < conflicts.Length; side++)
            {
                if (!conflicts[side] || !SharesFile(bases[0].Angles[side], bases[1].Angles[side])) continue;
                progress("正在使 " + bases[1].Position + " " + _cuts[side].Prefix + " 独立");
                if (ownerBefore == null) ownerBefore = AngleModel.CaptureAngleInstance(baseModel, ComponentById(baseModel, ownerId));
                MakeIndependent(root, RequireModel(bases[1].Owner), bases[1].Angles[side], _cuts[side].Prefix, numbers, output, owns);
                bases = RebindAfterIndependence(root, ids);
            }
            if (ownerBefore != null)
            {
                baseModel = Open(baseModel.GetPathName());
                Rebuild(baseModel);
                var ownerAfter = AngleModel.CaptureAngleInstance(baseModel, ComponentById(baseModel, ownerId));
                new FrameAngleIndependenceValidator().ValidateInstance("基座内子装配体", ownerBefore, ownerAfter,
                    ownerBefore.Path, preserveExistingMateErrors: true).RequireVerified();
                AngleModel.SaveAngleModel(baseModel);
            }
            if (!retainedPaths.SequenceEqual(bases[0].References.Select(reference => reference.GetPathName()), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("基座使之独立时改变了保留实例的引用：" + bases[0].Position);
            return bases;
        }

        private MountedBase[] RebindAfterIndependence(IModelDoc2 root, IReadOnlyDictionary<string, int> ids)
        {
            // 从总装重新取得嵌套实例，子装配体活动时的旧实例可能返回空引用配置。
            root = Open(root.GetPathName());
            SWHelper.EnsureAssemblyEditingContext(_app, root);
            Rebuild(root);
            return Locate(root, ids);
        }

        private void MakeIndependent(IModelDoc2 root, IModelDoc2 owner, IComponent2 occurrence, string prefix, IDrawingNumberGenerator numbers,
            string output, Func<string, bool> owns, IComponent2 retained = null, bool saveOwner = true, bool preserveExistingMateErrors = false)
        {
            owner = Open(owner.GetPathName());
            SWHelper.EnsureAssemblyEditingContext(_app, owner);
            Rebuild(owner);
            var id = occurrence.GetID();
            var local = ComponentById(owner, id);
            var before = AngleModel.CaptureAngleInstance(owner, local);
            var other = retained == null ? null : AngleModel.CaptureAngleInstance(owner, ComponentById(owner, retained.GetID()));
            if (!SharesFile(local, occurrence))
                throw new InvalidOperationException("基座独立化前的实际引用与计划不符：" + occurrence.Name2);
            var dependencies = new SolidWorksParentDimensionDriver(_app, owns);
            var inherited = dependencies.Capture(root, occurrence);
            var number = numbers.PeekNextDrawingNumber();
            var path = Path.Combine(output, prefix + numbers.PeekNextFileNameSuffix() + Path.GetExtension(before.Path));
            RequireOwned(owns, path);
            if (File.Exists(path)) throw new IOException("基座独立化目标文件已存在：" + path);
            Directory.CreateDirectory(output);
            owner.ClearSelection2(true);
            try
            {
                var selection = owner.SelectionManager as ISelectionMgr;
                if (!local.Select4(false, null, false) || selection?.GetSelectedObjectCount2(-1) != 1 ||
                    (selection.GetSelectedObject6(1, -1) as IComponent2)?.GetID() != id ||
                    !SolidWorksFileHelper.InvokeMakeIndependent(owner, path) || !File.Exists(path))
                    throw new InvalidOperationException("基座模型使之独立失败：" + before.Name + " / " + path);
            }
            finally { owner.ClearSelection2(true); }

            owner = Open(owner.GetPathName());
            Rebuild(owner);
            var validator = new FrameAngleIndependenceValidator();
            var current = AngleModel.CaptureAngleInstance(owner, ComponentById(owner, id));
            validator.ValidateInstance("基座独立实例", before, current, path, preserveExistingMateErrors).RequireVerified();
            if (other != null)
                validator.ValidateInstance("基座保留实例", other,
                    AngleModel.CaptureAngleInstance(owner, ComponentById(owner, other.Id)), other.Path).RequireVerified();

            dependencies.Restore(root, inherited);

            var model = Open(path);
            AngleModel.RequireDefaultReference(model, current.Configuration, current.Name);
            var drawingNumber = Path.GetFileNameWithoutExtension(path);
            _driver.SetCustomProperty(model, "图号", drawingNumber);
            var properties = model.Extension.CustomPropertyManager[current.Configuration];
            properties.Add3("图号", (int)swCustomInfoType_e.swCustomInfoText, drawingNumber, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            properties.Get4("图号", false, out var raw, out _);
            if (raw != drawingNumber || _driver.GetCustomProperty(model, "图号") != drawingNumber)
                throw new InvalidOperationException("基座独立模型图号反读不符：" + path);
            AngleModel.SaveAngleModel(model);
            owner = Open(owner.GetPathName());
            if (saveOwner) AngleModel.SaveAngleModel(owner);
            numbers.CommitNext(number);
        }

        private Body2[] CandidateBodies(IComponent2 angle, int side, BaseBridgeAngleNotchTarget target, Func<string, bool> owns)
        {
            var source = RequireModel(angle);
            var path = source.GetPathName();
            RequireOwned(owns, path);
            var copyPath = Path.Combine(Path.GetTempPath(), "dq-base-angle-" + Guid.NewGuid().ToString("N") + ".SLDPRT");
            Exception failure = null;
            try
            {
                if (source.GetSaveFlag())
                {
                    source = Open(path);
                    var errors = 0;
                    var warnings = 0;
                    if (!source.Extension.SaveAs(copyPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Copy | swSaveAsOptions_e.swSaveAsOptions_Silent), null, ref errors, ref warnings))
                        throw new IOException("无法复制当前基座角钢实体：" + path + " / " + errors);
                }
                else File.Copy(path, copyPath);
                var copy = OpenCandidate(copyPath);
                AngleModel.RequireDefaultReference(copy, angle.ReferencedConfiguration, angle.Name2);
                var cut = _cuts[side];
                AngleModel.WriteBaseBridgeAngleNotch(copy, cut.Feature, cut.Dimension, target);
                Rebuild(copy);
                if (!ReadTarget(copy, angle.ReferencedConfiguration, side, angle.Name2).Matches(target))
                    throw new InvalidOperationException("基座角钢候选豁口反读不符：" + angle.Name2);
                var bodies = ((IPartDoc)copy).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>();
                if (bodies.Length == 0) throw new InvalidOperationException("基座角钢候选缺少实体：" + angle.Name2);
                return bodies.OfType<Body2>().Select(b => (Body2)b.Copy()).ToArray();
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try
                {
                    var opened = _app.GetOpenDocumentByName(copyPath) as IModelDoc2;
                    if (opened != null) _app.CloseDoc(opened.GetTitle());
                    if (_app.GetOpenDocumentByName(copyPath) != null)
                        throw new IOException("基座角钢候选副本未确认关闭：" + copyPath);
                    File.Delete(copyPath);
                }
                catch (Exception cleanupError) when (failure != null)
                { FaceToWallPreviewTrace.Error("清理基座角钢候选失败：" + copyPath, cleanupError); }
            }
        }

        private IModelDoc2 OpenCandidate(string path)
        {
            if (_template != "030") return Open(path);
            // 030 试算只需要零件实体，后台打开可避免临时窗口与总装互相切换。
            const int partType = (int)swDocumentTypes_e.swDocPART;
            var visible = _app.GetDocumentVisible(partType);
            var errors = 0;
            var warnings = 0;
            IModelDoc2 model;
            try
            {
                _app.DocumentVisible(false, partType);
                model = _app.OpenDoc6(path, partType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty, ref errors, ref warnings) as IModelDoc2;
            }
            finally { _app.DocumentVisible(visible, partType); }
            if (errors != 0 || !BridgeGenerationFileBackup.Same(model?.GetPathName(), path))
                throw new InvalidOperationException("无法后台打开030角钢试算副本：" + path + "；错误码=" + errors);
            AngleModel.UseDefaultModelConfiguration(model);
            return model;
        }

        private IModelDoc2 Open(string path)
        {
            var model = _driver.OpenOrActivateDocument(path, rebuildAfterResolve: false);
            AngleModel.UseDefaultModelConfiguration(model);
            return model;
        }

        private double Depth(IComponent2 angle, int side) => AngleModel.ReadNotchDepth(RequireModel(angle),
            angle.ReferencedConfiguration, _cuts[side].Feature, _cuts[side].Dimension, angle.Name2);

        private BaseBridgeAngleNotchTarget ReadTarget(IComponent2 angle, int side) =>
            ReadTarget(RequireModel(angle), angle.ReferencedConfiguration, side, angle.Name2);

        private BaseBridgeAngleNotchTarget ReadTarget(IModelDoc2 model, string configuration, int side, string label)
        {
            var cut = _cuts[side];
            var active = AngleModel.ReadNotchDepth(model, configuration, cut.Feature, cut.Dimension, label);
            // 压缩时ReadNotchDepth返回逻辑档位0，仍须独立反读潜在尺寸。
            var depth = ReadDimension(model, configuration, cut.Dimension, label);
            return new BaseBridgeAngleNotchTarget(depth, active == 0d,
                _template == "030" ? (double?)null : ReadDimension(model, configuration, "D1@草图2", label),
                _template == "030" ? (double?)null : ReadDimension(model, configuration, "D3@草图2", label));
        }

        private static double ReadDimension(IModelDoc2 model, string configuration, string name, string label)
        {
            var values = (model.Parameter(name) as Dimension)?.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                new[] { configuration }) as double[];
            if (values?.Length != 1 || !(values[0] > 0d) || double.IsInfinity(values[0]))
                throw new InvalidOperationException("无法反读基座角钢原尺寸：" + label + "/" + name);
            return values[0] * 1000d;
        }

        private void RequireTarget(IComponent2 angle, int side, BaseBridgeAngleNotchTarget expected)
        {
            if (!ReadTarget(angle, side).Matches(expected))
                throw new InvalidOperationException("基座角钢完整豁口反读不符（长度、位置、深度或压缩状态）：" + angle.Name2);
        }

        private static void RequireNoOtherReferences(IModelDoc2 root, IEnumerable<MountedBase> bases)
        {
            var references = bases.SelectMany(b => b.References).ToArray();
            var names = new HashSet<string>(references.Select(c => c.Name2), StringComparer.Ordinal);
            var paths = new HashSet<string>(references.Select(c => c.GetPathName()), StringComparer.OrdinalIgnoreCase);
            var others = (((IAssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<IComponent2>()
                .FirstOrDefault(c => !c.IsSuppressed() && paths.Contains(c.GetPathName()) && !names.Contains(c.Name2));
            if (others != null) throw new InvalidOperationException("基座驱动文件被本阶段以外的组件共用：" + others.Name2);
        }

        private static Body2[] SolidBodies(IComponent2 component)
        {
            var bodies = (component.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[] ?? Array.Empty<object>()).OfType<Body2>().ToArray();
            if (bodies.Length == 0) throw new InvalidOperationException("缺少可测量的实际零件实体：" + component.Name2);
            return bodies;
        }

        private static Body2 CopyBody(Body2 source, MathTransform transform, string label)
        {
            var body = source.Copy() as Body2;
            if (body == null || transform == null || !body.ApplyTransform(transform))
                throw new InvalidOperationException("无法把测量实体变换到总装坐标系：" + label);
            return body;
        }

        private static double Distance(IModelDoc2 root, object angle, object copper, string label, string copperName)
        {
            var distance = root.ClosestDistance(angle, copper, out _, out _) * 1000d;
            if (!(distance >= 0d) || double.IsInfinity(distance))
                throw new InvalidOperationException("无法测得基座桥端角钢绝缘距离：" + label + " / " + copperName);
            return distance;
        }

        private static void Rebuild(IModelDoc2 model)
        {
            if (!model.EditRebuild3()) throw new InvalidOperationException("基座角钢驱动后模型求解失败：" + model.GetPathName());
        }

        private static IComponent2 ComponentById(IModelDoc2 owner, int id) =>
            ((IAssemblyDoc)owner).GetComponentByID(id) as IComponent2 ??
                throw new InvalidOperationException("基座组件ID已失效：" + owner.GetPathName() + "/" + id);

        private static IModelDoc2 RequireModel(IComponent2 component)
        {
            var model = component.GetModelDoc2() as IModelDoc2;
            AngleModel.RequireDefaultReference(model, component.ReferencedConfiguration, component.Name2);
            AngleModel.RequireDefaultReference(model, model.ConfigurationManager.ActiveConfiguration.Name, component.Name2);
            return model;
        }

        private static IComponent2[] Children(IComponent2 component) =>
            (component.GetChildren() as object[] ?? Array.Empty<object>()).OfType<IComponent2>().Where(c => !c.IsSuppressed()).ToArray();

        private static bool MatchesPath(IComponent2 component, string prefix) =>
            AngleModel.SmallOffsetSealPlateIdentifierMatches(component.GetPathName(), prefix);

        private static bool FinitePosition(double[] transform) => transform.Skip(9).Take(3).All(v => !double.IsNaN(v) && !double.IsInfinity(v));

        private static bool SharesFile(IComponent2 a, IComponent2 b) => BridgeGenerationFileBackup.Same(a.GetPathName(), b.GetPathName());

        private static void RequireOwned(Func<string, bool> owns, string path)
        {
            if (!owns(path)) throw new InvalidOperationException("基座桥端驱动引用超出事务范围：" + path);
        }
    }
}
