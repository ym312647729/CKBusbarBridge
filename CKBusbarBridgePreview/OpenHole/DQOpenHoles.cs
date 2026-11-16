using AutoOpeningHoleBase;
using AutoOpeningHoleDataModel;
using GeometRi;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview.OpenHole
{
    public static class DQOpenHoles
    {
        public static readonly Vector3d[] Xyz = new Vector3d[]
        {
            new Vector3d(1, 0, 0), new Vector3d(-1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, -1, 0),
            new Vector3d(0, 0, 1), new Vector3d(0, 0, -1)
        };

        public static HoleInfoOnFaceAndName GetHoleNameInAssUseMatingFaceOpenHoleInfo(
           this MatingFaceOpenHoleInfo OpenHoleInfo, Point3d AssCenterP, Vector3d Normal
           , Vector3d CompV, bool allowNonOrthogonalFace = false)
        {
            if (OpenHoleInfo == null)
            {
                return null;
            }

            if (!allowNonOrthogonalFace &&
                !Xyz.Any(x => x.IsParallelToTol(Normal, 0.15)))
            {
                return null;
            }

            bool IsPositive = Normal.Normalized.ToArrayData().Sum() > 0;

            HoleInfoOnFaceAndName holeName = new HoleInfoOnFaceAndName();
            var str = Guid.NewGuid().ToString().Substring(0, 6);
            holeName.featureName = $"铜排开孔-{str}";
            holeName.sketchName = $"铜排开孔草图-{str}";

            List<HoleInfoOnFace> existHoleInfos = new List<HoleInfoOnFace>();

            var pointsLocation = OpenHoleInfo.HoleLocation.Split(';');

            var vY = CompV;
            var vX = IsPositive ? vY.Cross(Normal) : vY.Cross(Normal.Negate());

            foreach (var tempPoints in pointsLocation)
            {
                var tempPoint = tempPoints.Split(',');

                var tempPointX = double.Parse(
                    tempPoint[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture);
                var tempPointY = double.Parse(
                    tempPoint[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture);

                var HoleInfo = new HoleInfoOnFace();

                HoleInfo.Vector = Normal.Normalized;
                HoleInfo.Plane3D = new Plane3d(AssCenterP, Normal);

                if (OpenHoleInfo.HoleStyle == "圆孔")
                {
                    HoleInfo.HoleType = ToolType_e.Circle;
                    HoleInfo.dimD = OpenHoleInfo.HoleD;
                    HoleInfo.CenterPoint = AssCenterP.Move(vX, tempPointX).Move(vY, tempPointY);
                }
                else
                {
                    HoleInfo.HoleType = ToolType_e.Slot;
                    HoleInfo.dimD = OpenHoleInfo.HoleD;
                    HoleInfo.dimY = OpenHoleInfo.HoleY;

                    var offset = (HoleInfo.dimY - HoleInfo.dimD) / 2.0;

                    HoleInfo.CenterPoint = AssCenterP.Move(vX, tempPointX - offset).Move(vY, tempPointY);
                    HoleInfo.CenterPoint2 = AssCenterP.Move(vX, tempPointX + offset).Move(vY, tempPointY);
                }

                existHoleInfos.Add(HoleInfo);
            }

            holeName.thisHole = existHoleInfos;

            return holeName;
        }

        public static void OpenAllBusbarHoles2(this ISldWorks swApp,
            Component2 BusbarAssComp, List<Component2> Insulations, double BusbarWidth)
        {
            try
            {
                DateTime t = DateTime.Now;

                var swAssModel = swApp.IActiveDoc2;
                var swAssDoc = swAssModel as AssemblyDoc;
                var busCompPath = BusbarAssComp.GetPathName();

                MatingFaceOpenHoleInfo OpenHoleInfo = DataHelper.GetMatingFaceOpenHole(BusbarWidth, BusbarWidth,
                    "水平", AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(),
                    AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(), BusbarWidth,
                    BusbarWidth);

                var busComps = ((object[])swAssDoc.GetComponents(false)).Cast<Component2>()
                    .ToList();

                busComps = busComps.Where(x =>
                    x != null && !string.IsNullOrWhiteSpace(x.GetPathName()) &&
                    x.IGetModelDoc()?.GetType() == (int)swDocumentTypes_e.swDocPART
                    && x.IGetModelDoc().GetModelType() >= PartType_e.Busbar).ToList();

                busComps = busComps
                    .Where(GeneratedBusbarSubassemblyIdentity
                        .IsUnderGeneratedSubassembly)
                    .ToList();

                busComps = busComps
                    .GroupBy(component => component.IGetModelDoc())
                    .Select(componentGroup => componentGroup
                        .OrderBy(component => component.Name2 ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderBy(component => component.GetPathName() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(component => component.Name2 ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();
                
                List<Tuple<string, List<HoleInfoOnFaceAndName>>> tuples = new List<Tuple<string, List<HoleInfoOnFaceAndName>>>();
                foreach (var thisComp in busComps)
                {
                    var compASegs = thisComp.GetSegmentsInAss(swApp);
                    if (compASegs.Count < 1)
                    {
                        continue;
                    }

                    var startP = compASegs[0].P1.Move(compASegs[0].Direction, BusbarWidth / 2.0);

                    var startFaceV = swApp
                        .GetFaceByObj(thisComp,
                            compASegs[0].GetMiddlePoint2().ConvertPointInAssemblyToPart1000(swApp, thisComp))
                        .ToPlane3d().Normal.ConvertVectorPartToAssembly(swApp, thisComp);

                    var endP = compASegs[compASegs.Count - 1].P2.Move(compASegs[compASegs.Count - 1].Direction.Negate(),
                        BusbarWidth / 2.0);

                    var endFaceV = swApp
                        .GetFaceByObj(thisComp,
                            compASegs[compASegs.Count - 1].GetMiddlePoint2()
                                .ConvertPointInAssemblyToPart1000(swApp, thisComp))
                        .ToPlane3d().Normal.ConvertVectorPartToAssembly(swApp, thisComp);

                    var compV = new Vector3d(0, 1, 0).ConvertVectorPartToAssembly(swApp, thisComp);

                    var MatingFHoleNames = new List<HoleInfoOnFaceAndName>();
                    MatingFHoleNames.Add(
                        OpenHoleInfo.GetHoleNameInAssUseMatingFaceOpenHoleInfo(startP, startFaceV, compV));

                    MatingFHoleNames.Add(
                        OpenHoleInfo.GetHoleNameInAssUseMatingFaceOpenHoleInfo(endP, endFaceV, compV));
                    
                    foreach (var Insulation in Insulations)
                    {
                        var InsulationPoint = new Point3d().ConvertPointInPartToAssembly1000(swApp, Insulation);

                        InsulationPoint = InsulationPoint.ProjectionTo(compASegs[0].ToLine);

                        if (InsulationPoint.JudgePointInSegment(compASegs[0]))
                        {
                            HoleInfoOnFaceAndName holeName = new HoleInfoOnFaceAndName();
                            var str = Guid.NewGuid().ToString().Substring(0, 6);
                            holeName.featureName = $"绝缘子开孔-{str}";
                            holeName.sketchName = $"绝缘子开孔草图-{str}";

                            var HoleInfo = new HoleInfoOnFace();

                            HoleInfo.Vector = startFaceV.ConvertVectorAssemblyToPart(swApp, BusbarAssComp)
                                .Normalized;
                            HoleInfo.Plane3D =
                                new Plane3d(InsulationPoint, startFaceV).ConvertPlaneInAssemblyToPart1000(swApp,
                                    BusbarAssComp);

                            HoleInfo.HoleType = ToolType_e.Circle;
                            HoleInfo.dimD = OpenHoleInfo.HoleD;
                            HoleInfo.CenterPoint =
                                InsulationPoint.ConvertPointInAssemblyToPart1000(swApp, BusbarAssComp);

                            holeName.thisHole = new List<HoleInfoOnFace>() { HoleInfo };

                            MatingFHoleNames.Add(holeName);
                        }
                    }

                    tuples.Add(new Tuple<string, List<HoleInfoOnFaceAndName>>(thisComp.GetPathName(),
                        MatingFHoleNames));
                }
                
                var thisPath = swApp.IActiveDoc2.GetPathName();
                swApp.CloseDoc(thisPath);
                
                int err = 0;
                int warn = 0;
                //swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocASSEMBLY);
                swApp.OpenDoc6(busCompPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);

                swApp.ActivateDoc(busCompPath);
                int index = 1;

                ModelViewReset modelViewReset = new ModelViewReset((SldWorks)swApp);
                modelViewReset.SaveModelView();

                try
                {
                    var busAssModel = swApp.IActiveDoc2;
                    busAssModel.LargeAssemblyMode = true;

                    var busComps2 = ((object[])(busAssModel as AssemblyDoc).GetComponents(false)).Cast<Component2>()
                        .ToList();

                    busComps2 = busComps2.Where(x =>
                        x != null && !string.IsNullOrWhiteSpace(x.GetPathName()) &&
                        x.IGetModelDoc()?.GetType() == (int)swDocumentTypes_e.swDocPART
                        && x.IGetModelDoc().GetModelType() >= PartType_e.Busbar).ToList();

                    busComps2 = busComps2
                        .GroupBy(component => component.IGetModelDoc())
                        .Select(componentGroup => componentGroup
                            .OrderBy(component => component.Name2 ?? string.Empty,
                                StringComparer.OrdinalIgnoreCase)
                            .First())
                        .OrderBy(component => component.GetPathName() ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(component => component.Name2 ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var thisComp in busComps2)
                    {
                        var tuple = tuples.Find(x => x.Item1 == thisComp.GetPathName());

                        if (tuple == null)
                        {
                            continue;
                        }

                        DateTime s = DateTime.Now;
                        swApp.CutUseHoleInfoOnFaceAndName(thisComp, tuple.Item2);

                        index++;
                        DateTime s1 = DateTime.Now;
                        FaceToWallPreviewTrace.Info(
                            "旧版铜排开孔-单个铜排完成",
                            "零件=" +
                            Path.GetFileNameWithoutExtension(thisComp.GetPathName()) +
                            "，耗时毫秒=" +
                            (s1 - s).TotalMilliseconds.ToString(
                                "0",
                                CultureInfo.InvariantCulture));
                    }

                    DateTime t1 = DateTime.Now;
                    FaceToWallPreviewTrace.Info(
                        "旧版铜排开孔-批处理完成",
                        "耗时毫秒=" +
                        (t1 - t).TotalMilliseconds.ToString(
                            "0",
                            CultureInfo.InvariantCulture));

                    busAssModel.Save();
                }
                catch (Exception e)
                {
                    FaceToWallPreviewTrace.Error(
                        "旧版铜排开孔-批处理失败",
                        e);
                    throw;
                }
                finally
                {
                    modelViewReset.ResetModelView(null);
                }
                
                swApp.CloseDoc(busCompPath);
                swAssModel = swApp.OpenDoc6(thisPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);

                swAssModel.LargeAssemblyMode = false;
            }
            catch (Exception e)
            {
                FaceToWallPreviewTrace.Error(
                    "旧版铜排开孔-执行失败",
                    e);
                throw;
            }
            finally
            {
               
            }
        }

        public static void OpenAllBusbarHoles(this ISldWorks swApp, Component2 BusbarAssComp, List<Component2> Insulations
    , BusDuctData busDuct, bool applyWallBushingHoleMapping = false,
            Action<int, int, string> progressReporter = null, bool requireComplete = false)
        {
            var swAssModel = swApp.IActiveDoc2;
            var swAssDoc = swAssModel as AssemblyDoc;
            ModelViewReset modelViewReset = new ModelViewReset((SldWorks)swApp);
            modelViewReset.SaveModelView();
            //swAssModel.LargeAssemblyMode = true;
            
            try
            {
                DateTime t = DateTime.Now;
          
                MatingFaceOpenHoleInfo OpenHoleInfo = DataHelper.GetMatingFaceOpenHole(busDuct.BendSetting.BusBarWidth, busDuct.BendSetting.BusBarWidth,
                    "水平", AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(),
                    AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(), busDuct.BendSetting.BusBarWidth,
                    busDuct.BendSetting.BusBarWidth);

                var busComps = ((object[])swAssDoc.GetComponents(false)).Cast<Component2>()
                    .ToList();

                Insulations = busComps.Where(x =>
                    x != null && x.IGetModelDoc() != null
                              && x.IGetModelDoc().GetType() == (int)swDocumentTypes_e.swDocPART
                              && Path.GetFileNameWithoutExtension(x.GetPathName()).Contains("绝缘子")
                ).ToList();
                
                busComps = busComps.Where(x =>
                    x != null && !string.IsNullOrWhiteSpace(x.GetPathName()) &&
                    x.IGetModelDoc()?.GetType() == (int)swDocumentTypes_e.swDocPART
                    && x.IGetModelDoc().GetModelType() >= PartType_e.Busbar
                    && !IsCabinetTopExtensionBusbar(x)).ToList();

                busComps = busComps
                    .Where(GeneratedBusbarSubassemblyIdentity
                        .IsUnderGeneratedSubassembly)
                    .ToList();
                FaceToWallPreviewTrace.Info(
                    "铜排开孔-按RD类型筛选",
                    "仅处理 RD类型=" +
                    GeneratedBusbarSubassemblyIdentity.RdTypePropertyValue +
                    " 下的铜排，数量=" + busComps.Count);

                busComps = busComps
                    .GroupBy(component => component.IGetModelDoc())
                    .Select(componentGroup => componentGroup
                        .OrderBy(component => component.Name2 ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderBy(component => component.GetPathName() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(component => component.Name2 ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 传入指定装配体时兼容旧调用，仅处理该子装配体；
                // 刷新开孔菜单传 null，改为处理活动总装配体中全部铜排。
                if (BusbarAssComp != null)
                {
                    var scopedBusbarAssembly = BusbarAssComp.IGetModelDoc() as AssemblyDoc;
                    var scopedBusbarComponents = scopedBusbarAssembly == null
                        ? new List<Component2>()
                        : ((object[])scopedBusbarAssembly.GetComponents(false))
                            .Cast<Component2>()
                            .ToList();

                    busComps = busComps.Where(x => scopedBusbarComponents.Any(
                        y => y.GetPathName() == x.GetPathName())).ToList();
                }

                var wallBushingBusbarComponents = applyWallBushingHoleMapping
                    ? FindWallBushingBusbarComponents(swApp)
                    : new List<Component2>();

                if (requireComplete && (busComps.Count == 0 || OpenHoleInfo == null))
                    throw new InvalidOperationException("铜排开孔缺少已生成的铜排或对应排宽的开孔工艺。");
                var holeFailures = new List<Exception>();
                progressReporter?.Invoke(0, busComps.Count, "准备刷新开孔");
                int index = 0;

                foreach (var thisComp in busComps)
                {
                    try
                    {
                        if (thisComp.IGetModelDoc().GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ||
                            thisComp.IGetModelDoc() == null || thisComp.GetPathName() == null ||
                            thisComp.IsHidden(true))
                        {
                            continue;
                        }

                        var compASegs = thisComp.GetSegmentsInAss(swApp);
                        if (compASegs.Count < 1)
                        {
                            if (requireComplete) throw new InvalidOperationException("铜排缺少可核验的原绘制线段。");
                            continue;
                        }

                        var isVerticalBendBusbar =
                            thisComp.IsVerticalBendBusbar();

                        if (thisComp.Name2.Contains("8C12345.0010"))
                        {
                            var str = "";
                        }

                        DeleteAllHoleInfoOnFaces(thisComp);
                        
                        var startP = compASegs[0].P1
                            .Move(compASegs[0].Direction, busDuct.BendSetting.BusBarWidth / 2.0);

                        var startFaceV = ResolveBusbarSegmentFaceNormal(
                            swApp,
                            thisComp,
                            compASegs[0],
                            isVerticalBendBusbar);

                        var endP = compASegs[compASegs.Count - 1].P2.Move(
                            compASegs[compASegs.Count - 1].Direction.Negate(),
                            busDuct.BendSetting.BusBarWidth / 2.0);

                        var endFaceV = ResolveBusbarSegmentFaceNormal(
                            swApp,
                            thisComp,
                            compASegs[compASegs.Count - 1],
                            isVerticalBendBusbar);
                        
                        if (startFaceV == null || endFaceV == null)
                        {
                            int openErrors = 0;
                            int openWarnings = 0;

                            swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                            var swModel = swApp.OpenDoc6(thisComp.GetPathName(), (int)swDocumentTypes_e.swDocPART,
                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref openErrors, ref openWarnings);
                            swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);

                            swApp.ActivateDoc(swModel.GetPathName());

                            thisComp.IGetModelDoc().EditRebuild3();
                           
                            startP = compASegs[0].P1
                                .Move(compASegs[0].Direction, busDuct.BendSetting.BusBarWidth / 2.0);

                            startFaceV = ResolveBusbarSegmentFaceNormal(
                                swApp,
                                thisComp,
                                compASegs[0],
                                isVerticalBendBusbar);

                            endP = compASegs[compASegs.Count - 1].P2.Move(
                                compASegs[compASegs.Count - 1].Direction.Negate(),
                                busDuct.BendSetting.BusBarWidth / 2.0);

                            endFaceV = ResolveBusbarSegmentFaceNormal(
                                swApp,
                                thisComp,
                                compASegs[compASegs.Count - 1],
                                isVerticalBendBusbar);

                            swApp.CloseDoc(thisComp.GetPathName());
                            if (startFaceV == null || endFaceV == null)
                            {
                                if (requireComplete) throw new InvalidOperationException("未找到对应的铜排端部开孔面。");
                                FaceToWallPreviewTrace.Warning(
                                    "铜排开孔-端面识别",
                                    "未找到对应开孔面，已跳过当前铜排。组件=" +
                                    (thisComp.Name2 ?? string.Empty));
                                continue;
                            }
                        }

                        var componentHoleWidthDirection = new Vector3d(0, 1, 0)
                            .ConvertVectorPartToAssembly(swApp, thisComp);
                        var startHoleWidthDirection = isVerticalBendBusbar
                            ? ResolveVerticalBendEndHoleWidthDirection(
                                compASegs[0],
                                startFaceV)
                            : componentHoleWidthDirection;
                        var endHoleWidthDirection = isVerticalBendBusbar
                            ? ResolveVerticalBendEndHoleWidthDirection(
                                compASegs[compASegs.Count - 1],
                                endFaceV)
                            : componentHoleWidthDirection;
                        var startEndFaceAlreadyContainsHole =
                            DoesBusbarSegmentFaceContainHole(
                                swApp,
                                thisComp,
                                compASegs[0],
                                isVerticalBendBusbar);
                        var finishEndFaceAlreadyContainsHole =
                            DoesBusbarSegmentFaceContainHole(
                                swApp,
                                thisComp,
                                compASegs[compASegs.Count - 1],
                                isVerticalBendBusbar);

                        var MatingFHoleNames = new List<HoleInfoOnFaceAndName>();

                        var wallBushingHoleMapping = BuildWallBushingHoleMapping(
                            swApp,
                            thisComp,
                            wallBushingBusbarComponents,
                            startP,
                            endP,
                            startFaceV,
                            endFaceV);

                        if (!wallBushingHoleMapping.SkipStartProcessHole &&
                            !startEndFaceAlreadyContainsHole)
                        {
                            var startEndHoleGroup = OpenHoleInfo
                                .GetHoleNameInAssUseMatingFaceOpenHoleInfo(
                                    startP,
                                    startFaceV,
                                    startHoleWidthDirection,
                                    isVerticalBendBusbar);
                            if (startEndHoleGroup != null)
                            {
                                MatingFHoleNames.Add(startEndHoleGroup);
                            }
                        }

                        if (!wallBushingHoleMapping.SkipEndProcessHole &&
                            !finishEndFaceAlreadyContainsHole)
                        {
                            var finishEndHoleGroup = OpenHoleInfo
                                .GetHoleNameInAssUseMatingFaceOpenHoleInfo(
                                    endP,
                                    endFaceV,
                                    endHoleWidthDirection,
                                    isVerticalBendBusbar);
                            if (finishEndHoleGroup != null)
                            {
                                MatingFHoleNames.Add(finishEndHoleGroup);
                            }
                        }

                        MatingFHoleNames.AddRange(wallBushingHoleMapping.HoleGroups);

                        foreach (var Insulation in Insulations)
                        {
                            PartInfo tempPartInfo = new PartInfo();

                            //转化成非轻化

                            //基准配合面信息 零件信息
                            MatingFacePreviewUtility.GetMatingFaceInformationToSw(Insulation.IGetModelDoc(),
                                ref tempPartInfo, (SldWorks)swApp);

                            if (tempPartInfo.SwPropertiesMatingFaces.Count == 1)
                            {
                                var skFeatureForPeiHe2 =
                                    (Feature)(Insulation.IGetModelDoc() as PartDoc).FeatureByName(tempPartInfo
                                        .SwPropertiesMatingFaces[0].MatingFaceName);

                                if (skFeatureForPeiHe2 != null)
                                {
                                    MatingFaceInfo matingFaceInfo2 = MatingFaceUtility.GetMatingFaceInfo(
                                        (SldWorks)swApp,
                                        (Sketch)skFeatureForPeiHe2.GetSpecificFeature2());

                                    if (matingFaceInfo2.IsCircle)
                                    {
                                        var InsulationPoint =
                                            matingFaceInfo2.CenterPoint.ConvertPointInPartToAssembly1000(swApp,
                                                Insulation);

                                        var InsulationV =
                                            matingFaceInfo2.Normal.ConvertVectorPartToAssembly(swApp, Insulation);

                                        foreach (var thisSeg in compASegs)
                                        {
                                            var face = swApp.GetFaceByObj(thisComp, thisSeg.GetMiddlePoint2()
                                                .ConvertPointInAssemblyToPart1000(swApp, thisComp));

                                            var facePlane = face.ToPlane3d()
                                                .ConvertPlaneInPartToAssembly1000(swApp, thisComp);

                                            if (facePlane.Normal.Normalized.IsParallelToTol(InsulationV.Normalized,
                                                    0.1))
                                            {
                                                // 4 叠排时搜索 7 个排厚，并额外保留 50 mm 的范围，
                                                // 以覆盖搭接区域边缘的绝缘子。
                                                if (InsulationPoint.DistanceTo(facePlane) >
                                                    busDuct.BendSetting.BusBarThickness * (busDuct.BusbarNum * 2 - 1) +
                                                    50)
                                                {
                                                    continue;
                                                }

                                                var thisPoint = InsulationPoint.ProjectionTo(facePlane)
                                                    .ConvertPointInAssemblyToPart1000(swApp, thisComp);

                                                if (thisPoint.JudgePointInFace(face))
                                                {
                                                    HoleInfoOnFaceAndName holeName = new HoleInfoOnFaceAndName();
                                                    var str = Guid.NewGuid().ToString().Substring(0, 6);
                                                    holeName.featureName = $"绝缘子开孔-{str}";
                                                    holeName.sketchName = $"绝缘子开孔草图-{str}";

                                                    var HoleInfo = new HoleInfoOnFace();

                                                    HoleInfo.Vector = InsulationV.Normalized;
                                                    HoleInfo.Plane3D =
                                                        new Plane3d(InsulationPoint, InsulationV);

                                                    HoleInfo.HoleType = ToolType_e.Circle;
                                                    HoleInfo.dimD = 13.0;
                                                    HoleInfo.CenterPoint = InsulationPoint.ProjectionTo(facePlane);

                                                    holeName.thisHole = new List<HoleInfoOnFace>() { HoleInfo };

                                                    MatingFHoleNames.Add(holeName);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        DateTime s = DateTime.Now;
                        swApp.CutUseHoleInfoOnFaceAndName(thisComp, MatingFHoleNames);

                        DateTime s1 = DateTime.Now;
                        FaceToWallPreviewTrace.Info(
                            "铜排开孔-单个铜排完成",
                            "零件=" +
                            Path.GetFileNameWithoutExtension(thisComp.GetPathName()) +
                            "，耗时毫秒=" +
                            (s1 - s).TotalMilliseconds.ToString(
                                "0",
                                CultureInfo.InvariantCulture));

                    }
                    catch (Exception e)
                    {
                        FaceToWallPreviewTrace.Error(
                            "铜排开孔-单个铜排处理失败",
                            e,
                            "零件=" +
                            Path.GetFileNameWithoutExtension(thisComp.GetPathName()));
                        if (requireComplete) holeFailures.Add(new InvalidOperationException(thisComp.Name2 + "：" + e.Message, e));
                    }
                    finally
                    {
                        index++;
                        progressReporter?.Invoke(
                            index,
                            busComps.Count,
                            Path.GetFileNameWithoutExtension(thisComp.GetPathName()));
                    }
                }

                // 换相段不是单纯的两端直排：旧预览会把相序转换拆成
                // 水平段 + 垂直搭接段 + 水平段。对这类排补跑旧版
                // SmartHoleProcess，并把比较范围限制为本次筛出的铜排，
                // 这样垂直搭接面也会按实际重合区域开孔，不会重新处理柜顶伸出排。
                RefreshVerticalPhaseChangeLapHoles(
                    swApp,
                    busComps,
                    progressReporter, requireComplete ? holeFailures : null);
                if (holeFailures.Count > 0) throw new AggregateException("铜排开孔未全部完成，生成事务保留恢复记录。", holeFailures);


                DateTime t1 = DateTime.Now;
                FaceToWallPreviewTrace.Info(
                    "铜排开孔-批处理完成",
                    "耗时毫秒=" +
                    (t1 - t).TotalMilliseconds.ToString(
                        "0",
                        CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                FaceToWallPreviewTrace.Error(
                    "铜排开孔-执行失败",
                    e);
                throw;
            }
            finally
            {
                modelViewReset.ResetModelView(null);
                //swAssModel.LargeAssemblyMode = false;
            }
        }

        #region 穿墙套管铜排孔映射

        private static void RefreshVerticalPhaseChangeLapHoles(
            ISldWorks swApp,
            IReadOnlyList<Component2> busbarComponents,
            Action<int, int, string> progressReporter, List<Exception> requiredFailures = null)
        {
            if (swApp == null || busbarComponents == null || busbarComponents.Count == 0)
            {
                return;
            }

            var phaseChangeComponents = busbarComponents
                .Where(component => IsPhaseChangeBusbarComponent(swApp, component))
                .ToList();
            if (phaseChangeComponents.Count == 0)
            {
                return;
            }

            var compareComponents = busbarComponents.ToList();
            var executedComponents = new List<Component2>();
            var phaseChangeIndex = 0;
            foreach (var phaseChangeComponent in phaseChangeComponents)
            {
                try
                {
                    progressReporter?.Invoke(
                        phaseChangeIndex,
                        phaseChangeComponents.Count,
                        "正在识别换相垂直搭接");

                    var autoOpeningHoleHelper = new AutoOpeningHoleHelper((SldWorks)swApp);
                    autoOpeningHoleHelper.SmartHoleProcess(
                        phaseChangeComponent,
                        excludeComponentList: executedComponents,
                        onlyCompareList: compareComponents,
                        isClearNoCoincidenceHole: false,
                        isForcedRefreshHole: false,
                        stackBusbarNotOpeningHole: false);
                    executedComponents.Add(phaseChangeComponent);
                }
                catch (Exception exception)
                {
                    // 垂直搭接是补充流程；某个模板不具备智能开孔数据时，
                    // 不影响本次已经完成的工艺库端孔和绝缘子孔。
                    FaceToWallPreviewTrace.Error(
                        "换相垂直搭接开孔失败",
                        exception);
                    requiredFailures?.Add(new InvalidOperationException("换相垂直搭接开孔失败：" + phaseChangeComponent.Name2, exception));
                }
                finally
                {
                    phaseChangeIndex++;
                    progressReporter?.Invoke(
                        phaseChangeIndex,
                        phaseChangeComponents.Count,
                        "换相垂直搭接处理完成");
                }
            }
        }

        private static bool IsPhaseChangeBusbarComponent(
            ISldWorks swApp,
            Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                var componentName = component.Name2 ?? string.Empty;
                var componentPath = component.GetPathName() ?? string.Empty;
                if (componentName.IndexOf("换相", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    componentPath.IndexOf("换相", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    componentName.IndexOf("phase", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    componentPath.IndexOf("phase", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                // 模板被重命名时按旧预览的几何特征兜底：至少三段，且存在
                // 相邻非平行段（水平段/垂直搭接段/水平段）。
                var segments = component.GetSegmentsInAss(swApp);
                if (segments == null || segments.Count < 3)
                {
                    return false;
                }

                for (var segmentIndex = 0; segmentIndex < segments.Count - 1; segmentIndex++)
                {
                    var firstDirection = segments[segmentIndex].ToVector.Normalized;
                    var secondDirection = segments[segmentIndex + 1].ToVector.Normalized;
                    if (Math.Abs(firstDirection.Dot(secondDirection)) < 0.25)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 无法读取几何时不把普通铜排误判成换相排。
            }

            return false;
        }

        /// <summary>
        /// 获取铜排端部线段所在面的装配体坐标系法向。
        /// 自由立弯必须直接使用基础草图线段中点匹配装配体中的对应面，
        /// 避免零件坐标转换后选中另一侧平行面。
        /// </summary>
        private static Vector3d ResolveBusbarSegmentFaceNormal(
            ISldWorks swApp,
            Component2 busbarComponent,
            Segment3d centerlineSegment,
            bool isVerticalBendBusbar)
        {
            if (swApp == null || busbarComponent == null || centerlineSegment == null)
            {
                return null;
            }

            var segmentMiddlePointInAssembly = centerlineSegment.GetMiddlePoint2();
            if (isVerticalBendBusbar)
            {
                var correspondingFaceInAssembly = swApp.GetFaceByObjInAss(
                    busbarComponent,
                    segmentMiddlePointInAssembly,
                    0.5d);
                var correspondingFacePlaneInAssembly =
                    correspondingFaceInAssembly?.ToPlane3d();
                if (correspondingFacePlaneInAssembly != null)
                {
                    return correspondingFacePlaneInAssembly.Normal;
                }
            }

            var segmentMiddlePointInPart = segmentMiddlePointInAssembly
                .ConvertPointInAssemblyToPart1000(swApp, busbarComponent);
            var correspondingFaceInPart = swApp.GetFaceByObj(
                busbarComponent,
                segmentMiddlePointInPart,
                0.5d);
            var correspondingFacePlaneInPart = correspondingFaceInPart?.ToPlane3d();
            return correspondingFacePlaneInPart?.Normal
                .ConvertVectorPartToAssembly(swApp, busbarComponent);
        }

        /// <summary>
        /// 按铜排大师的面孔识别方式检查中心线段对应的平面：有效内环
        /// 且内环边从该平面转入孔壁时，视为该面已经存在孔。
        /// </summary>
        private static bool DoesBusbarSegmentFaceContainHole(
            ISldWorks swApp,
            Component2 busbarComponent,
            Segment3d centerlineSegment,
            bool isVerticalBendBusbar)
        {
            if (swApp == null || busbarComponent == null ||
                centerlineSegment == null)
            {
                return false;
            }

            try
            {
                var segmentMiddlePointInAssembly =
                    centerlineSegment.GetMiddlePoint2();
                Face2 targetBusbarFace = null;
                if (isVerticalBendBusbar)
                {
                    targetBusbarFace = swApp.GetFaceByObjInAss(
                        busbarComponent,
                        segmentMiddlePointInAssembly,
                        0.5d);
                }

                if (targetBusbarFace == null)
                {
                    var segmentMiddlePointInPart =
                        segmentMiddlePointInAssembly
                            .ConvertPointInAssemblyToPart1000(
                                swApp,
                                busbarComponent);
                    targetBusbarFace = swApp.GetFaceByObj(
                        busbarComponent,
                        segmentMiddlePointInPart,
                        0.5d);
                }

                return DoesPlanarFaceContainHole(targetBusbarFace);
            }
            catch (Exception faceHoleDetectionException)
            {
                FaceToWallPreviewTrace.Error(
                    "检查铜排端面是否已有孔失败",
                    faceHoleDetectionException);
                return false;
            }
        }

        private static bool DoesPlanarFaceContainHole(Face2 targetBusbarFace)
        {
            if (targetBusbarFace?.ToPlane3d() == null)
            {
                return false;
            }

            var currentFaceLoop = targetBusbarFace.GetFirstLoop() as Loop2;
            while (currentFaceLoop != null)
            {
                var loopEdgeCount = currentFaceLoop.GetEdgeCount();
                var isRecognizedHoleEdgeCount = loopEdgeCount == 1 ||
                                                loopEdgeCount == 4 ||
                                                loopEdgeCount == 6;
                if (!currentFaceLoop.IsOuter() && isRecognizedHoleEdgeCount)
                {
                    var firstLoopCoEdge =
                        currentFaceLoop.GetFirstCoEdge() as CoEdge;
                    var partnerLoopCoEdge =
                        firstLoopCoEdge?.GetPartner() as CoEdge;
                    if (firstLoopCoEdge != null && partnerLoopCoEdge != null)
                    {
                        var currentFaceNormal =
                            GetFaceNormalAtCoEdgeMiddle(firstLoopCoEdge);
                        var partnerFaceNormal =
                            GetFaceNormalAtCoEdgeMiddle(partnerLoopCoEdge);
                        if (!AreSameDirectionVectors(
                                currentFaceNormal,
                                partnerFaceNormal))
                        {
                            var transitionDirection = CrossProduct(
                                currentFaceNormal,
                                partnerFaceNormal);
                            var loopEdgeTangent =
                                GetTangentAtCoEdgeMiddle(firstLoopCoEdge);
                            if (AreSameDirectionVectors(
                                    transitionDirection,
                                    loopEdgeTangent))
                            {
                                return true;
                            }
                        }
                    }
                }

                currentFaceLoop = currentFaceLoop.GetNext() as Loop2;
            }

            return false;
        }

        private static double[] GetFaceNormalAtCoEdgeMiddle(CoEdge faceCoEdge)
        {
            var coEdgeParameters = (double[])faceCoEdge.GetCurveParams();
            var middleParameter =
                (coEdgeParameters[6] + coEdgeParameters[7]) / 2d;
            var middleEvaluation = (double[])faceCoEdge.Evaluate(
                middleParameter);
            var ownerLoop = faceCoEdge.GetLoop() as Loop2;
            var ownerFace = ownerLoop?.GetFace() as Face2;
            var ownerSurface = ownerFace?.GetSurface() as Surface;
            var surfaceEvaluation = (double[])ownerSurface?.EvaluateAtPoint(
                middleEvaluation[0],
                middleEvaluation[1],
                middleEvaluation[2]);
            if (ownerFace == null || surfaceEvaluation == null ||
                surfaceEvaluation.Length < 3)
            {
                throw new InvalidOperationException(
                    "无法读取铜排孔轮廓相邻面的法向。");
            }

            var normalDirectionFactor = ownerFace.FaceInSurfaceSense()
                ? -1d
                : 1d;
            return new[]
            {
                surfaceEvaluation[0] * normalDirectionFactor,
                surfaceEvaluation[1] * normalDirectionFactor,
                surfaceEvaluation[2] * normalDirectionFactor
            };
        }

        private static double[] GetTangentAtCoEdgeMiddle(CoEdge faceCoEdge)
        {
            var coEdgeParameters = (double[])faceCoEdge.GetCurveParams();
            var middleParameter =
                (coEdgeParameters[6] + coEdgeParameters[7]) / 2d;
            var middleEvaluation = (double[])faceCoEdge.Evaluate(
                middleParameter);
            return new[]
            {
                middleEvaluation[3],
                middleEvaluation[4],
                middleEvaluation[5]
            };
        }

        private static double[] CrossProduct(
            IReadOnlyList<double> firstVector,
            IReadOnlyList<double> secondVector)
        {
            return new[]
            {
                firstVector[1] * secondVector[2] -
                firstVector[2] * secondVector[1],
                firstVector[2] * secondVector[0] -
                firstVector[0] * secondVector[2],
                firstVector[0] * secondVector[1] -
                firstVector[1] * secondVector[0]
            };
        }

        private static bool AreSameDirectionVectors(
            IReadOnlyList<double> firstVector,
            IReadOnlyList<double> secondVector)
        {
            var firstVectorLength = Math.Sqrt(
                firstVector[0] * firstVector[0] +
                firstVector[1] * firstVector[1] +
                firstVector[2] * firstVector[2]);
            var secondVectorLength = Math.Sqrt(
                secondVector[0] * secondVector[0] +
                secondVector[1] * secondVector[1] +
                secondVector[2] * secondVector[2]);
            if (firstVectorLength <= 0.0000000001d ||
                secondVectorLength <= 0.0000000001d)
            {
                return false;
            }

            var normalizedDotProduct =
                (firstVector[0] * secondVector[0] +
                 firstVector[1] * secondVector[1] +
                 firstVector[2] * secondVector[2]) /
                (firstVectorLength * secondVectorLength);
            return Math.Abs(normalizedDotProduct - 1d) < 0.0000000001d;
        }

        private static Vector3d ResolveVerticalBendEndHoleWidthDirection(
            Segment3d endSegment,
            Vector3d endFaceNormal)
        {
            if (endSegment == null ||
                endSegment.Direction == null ||
                endSegment.Direction.Norm <= 0.000001d ||
                endFaceNormal == null ||
                endFaceNormal.Norm <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "自由立弯端部线段或端部面法向无效，无法建立开孔坐标系。");
            }

            var normalizedEndFaceNormal = endFaceNormal.Normalized;
            var normalizedHolePlaneNormal = normalizedEndFaceNormal
                    .ToArrayData()
                    .Sum() > 0d
                ? normalizedEndFaceNormal
                : normalizedEndFaceNormal.Negate();
            var endHoleWidthDirection = normalizedHolePlaneNormal.Cross(
                endSegment.Direction.Normalized);
            if (endHoleWidthDirection.Norm <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "自由立弯端部线段与开孔面法向平行，无法建立开孔坐标系。");
            }

            return endHoleWidthDirection.Normalized;
        }

        private static bool IsCabinetTopExtensionBusbar(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                if (component.GetSystemPartType() == SystemPartType_e.CabinetTopExtensionBusbar)
                {
                    return true;
                }

                var componentName = component.Name2 ?? string.Empty;
                return componentName.IndexOf("柜顶伸出排", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       componentName.IndexOf("柜内伸出排", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private sealed class WallBushingHoleMappingResult
        {
            public bool SkipStartProcessHole { get; set; }
            public bool SkipEndProcessHole { get; set; }
            public List<HoleInfoOnFaceAndName> HoleGroups { get; } =
                new List<HoleInfoOnFaceAndName>();
        }

        private sealed class WallBushingFaceHoleSet
        {
            public Plane3d PlaneInAssembly { get; set; }
            public List<Point3d> BoundaryInAssembly { get; set; }
            public List<HoleInfoOnFace> Holes { get; set; }
        }

        /// <summary>
        /// 查找穿墙套管装配体下实际承载铜排几何的零件。
        /// 这里不依赖固定文件名，沿用预览阶段的系统零件分类和子树扫描。
        /// </summary>
        private static List<Component2> FindWallBushingBusbarComponents(ISldWorks swApp)
        {
            var result = new List<Component2>();
            try
            {
                var catalog = AssemblyPartCatalog.Create(swApp);
                var candidates = new List<Component2>();
                foreach (var wallBushing in catalog.GetComponents(SystemPartType_e.WallBushing))
                {
                    if (wallBushing != null)
                    {
                        candidates.Add(wallBushing);
                    }

                    candidates.AddRange(catalog.GetAllDescendants(wallBushing));
                }

                foreach (var candidate in candidates)
                {
                    ModelDoc2 candidateModel = null;
                    try
                    {
                        candidateModel = candidate?.IGetModelDoc();
                    }
                    catch
                    {
                        // 轻化组件无法读取模型时跳过，其他穿墙套管候选仍可处理。
                    }

                    if (candidate == null || candidateModel == null ||
                        candidateModel.GetType() != (int)swDocumentTypes_e.swDocPART ||
                        candidateModel.GetModelType() < PartType_e.Busbar)
                    {
                        continue;
                    }

                    var key = (candidate.GetPathName() ?? string.Empty) + "|" +
                              (candidate.Name2 ?? string.Empty);
                    if (!result.Any(existing =>
                            string.Equals(
                                (existing.GetPathName() ?? string.Empty) + "|" +
                                (existing.Name2 ?? string.Empty),
                                key,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(candidate);
                    }
                }
            }
            catch (Exception exception)
            {
                FaceToWallPreviewTrace.Error(
                    "读取穿墙套管铜排失败",
                    exception);
            }

            return result;
        }

        private static WallBushingHoleMappingResult BuildWallBushingHoleMapping(
            ISldWorks swApp,
            Component2 targetBusbar,
            IReadOnlyList<Component2> wallBushingBusbars,
            Point3d startPoint,
            Point3d endPoint,
            Vector3d startNormal,
            Vector3d endNormal)
        {
            var result = new WallBushingHoleMappingResult();
            if (swApp == null || targetBusbar == null ||
                wallBushingBusbars == null || wallBushingBusbars.Count == 0)
            {
                return result;
            }

            var targetFaces = GetPlanarFaces(targetBusbar);
            if (targetFaces.Count == 0)
            {
                return result;
            }

            var mappedHoleKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceBusbar in wallBushingBusbars)
            {
                foreach (var sourceFace in GetPlanarFaces(sourceBusbar))
                {
                    var sourceHoleSet = TryReadWallBushingFaceHoleSet(
                        swApp,
                        sourceBusbar,
                        sourceFace);
                    if (sourceHoleSet == null || sourceHoleSet.Holes.Count == 0)
                    {
                        continue;
                    }

                    foreach (var targetFace in targetFaces)
                    {
                        Plane3d targetPlane;
                        if (!TryGetFacePlaneInAssembly(swApp, targetBusbar, targetFace, out targetPlane) ||
                            !AreCoplanarAndParallel(sourceHoleSet.PlaneInAssembly, targetPlane) ||
                            !DoBusbarPolygonsOverlap(
                                sourceHoleSet.BoundaryInAssembly,
                                GetFaceBoundaryInAssembly(swApp, targetBusbar, targetFace)) )
                        {
                            continue;
                        }

                        var targetNormal = targetPlane.Normal.Normalized;
                        var holeInfosForTarget = new List<HoleInfoOnFace>();
                        foreach (var sourceHole in sourceHoleSet.Holes)
                        {
                            var mappedHole = ConvertHoleToTargetPlane(
                                sourceHole,
                                targetPlane,
                                targetNormal);
                            var isMappedHoleOnTargetFace = false;
                            try
                            {
                                isMappedHoleOnTargetFace = mappedHole != null &&
                                    mappedHole.CenterPoint
                                        .ConvertPointInAssemblyToPart1000(swApp, targetBusbar)
                                        .JudgePointInFace(targetFace);
                                if (isMappedHoleOnTargetFace && mappedHole.CenterPoint2 != null)
                                {
                                    isMappedHoleOnTargetFace = mappedHole.CenterPoint2
                                        .ConvertPointInAssemblyToPart1000(swApp, targetBusbar)
                                        .JudgePointInFace(targetFace);
                                }
                            }
                            catch
                            {
                                isMappedHoleOnTargetFace = false;
                            }

                            if (!isMappedHoleOnTargetFace)
                            {
                                continue;
                            }

                            var holeKey = BuildHoleKey(mappedHole);
                            if (mappedHoleKeys.Add(holeKey))
                            {
                                holeInfosForTarget.Add(mappedHole);
                            }
                        }

                        if (holeInfosForTarget.Count == 0)
                        {
                            continue;
                        }

                        var mappingName = Guid.NewGuid().ToString("N").Substring(0, 8);
                        result.HoleGroups.Add(new HoleInfoOnFaceAndName
                        {
                            featureName = "穿墙套管铜排开孔-" + mappingName,
                            sketchName = "穿墙套管铜排开孔草图-" + mappingName,
                            thisHole = holeInfosForTarget
                        });

                        // 只有目标面确实是工艺库的端面时，才跳过该端面的默认孔。
                        // 其他重叠面仍保留工艺库端孔，避免误删普通排两端的开孔。
                        if (IsProcessEndFace(targetPlane, startPoint, startNormal))
                        {
                            result.SkipStartProcessHole = true;
                        }

                        if (IsProcessEndFace(targetPlane, endPoint, endNormal))
                        {
                            result.SkipEndProcessHole = true;
                        }
                    }
                }
            }

            return result;
        }

        private static bool IsProcessEndFace(
            Plane3d targetPlane,
            Point3d processPoint,
            Vector3d processNormal)
        {
            if (targetPlane == null || processPoint == null || processNormal == null)
            {
                return false;
            }

            return targetPlane.Normal.Normalized.IsParallelToTol(processNormal.Normalized, 0.1) &&
                   Math.Abs(processPoint.DistanceTo(targetPlane)) <= 2.0;
        }

        private static List<Face2> GetPlanarFaces(Component2 component)
        {
            var result = new List<Face2>();
            try
            {
                var partDoc = component?.IGetModelDoc() as PartDoc;
                var bodies = partDoc?.GetBodies(0) as object[];
                if (bodies == null)
                {
                    return result;
                }

                foreach (var body in bodies.OfType<Body2>())
                {
                    var faces = body.GetFaces() as object[];
                    if (faces == null)
                    {
                        continue;
                    }

                    foreach (var face in faces.OfType<Face2>())
                    {
                        try
                        {
                            if (face != null && face.ToPlane3d() != null)
                            {
                                result.Add(face);
                            }
                        }
                        catch
                        {
                            // 非平面面不能用于孔映射，忽略即可。
                        }
                    }
                }
            }
            catch
            {
                // 保持旧版开孔流程可用，单个异常不应中断整个刷新。
            }

            return result;
        }

        private static WallBushingFaceHoleSet TryReadWallBushingFaceHoleSet(
            ISldWorks swApp,
            Component2 sourceComponent,
            Face2 sourceFace)
        {
            try
            {
                Plane3d sourcePlane;
                if (!TryGetFacePlaneInAssembly(swApp, sourceComponent, sourceFace, out sourcePlane))
                {
                    return null;
                }

                var boundary = GetFaceBoundaryInAssembly(swApp, sourceComponent, sourceFace);
                if (boundary.Count < 3)
                {
                    return null;
                }

                var holes = ReadHoleInfosOnFace(swApp, sourceComponent, sourceFace, sourcePlane);
                return new WallBushingFaceHoleSet
                {
                    PlaneInAssembly = sourcePlane,
                    BoundaryInAssembly = boundary,
                    Holes = holes
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从穿墙套管铜排面的内环读取圆孔和腰孔，逻辑对应 BusbarTools1 的
        /// MappingHoleHelper.GetAllHolesInfoOnFace；穿墙套管没有配合面时直接读取 Loop。
        /// </summary>
        private static List<HoleInfoOnFace> ReadHoleInfosOnFace(
            ISldWorks swApp,
            Component2 sourceComponent,
            Face2 sourceFace,
            Plane3d sourcePlaneInAssembly)
        {
            var result = new List<HoleInfoOnFace>();
            var loops = sourceFace?.GetLoops() as object[];
            if (loops == null)
            {
                return result;
            }

            foreach (var loop in loops.OfType<Loop2>())
            {
                if (loop == null || loop.IsOuter())
                {
                    continue;
                }

                var edges = loop.GetEdges() as object[];
                if (edges == null || edges.Length == 0)
                {
                    continue;
                }

                var circleEdges = new List<Edge>();
                var lineEdges = 0;
                foreach (var edge in edges.OfType<Edge>())
                {
                    var curve = edge.GetCurve() as Curve;
                    if (curve == null)
                    {
                        continue;
                    }

                    if (curve.IsCircle())
                    {
                        circleEdges.Add(edge);
                    }
                    else if (curve.IsLine())
                    {
                        lineEdges++;
                    }
                }

                if (circleEdges.Count == 1 && edges.Length == 1)
                {
                    var circle = circleEdges[0].GetCurve() as Curve;
                    var parameters = circle?.CircleParams as double[];
                    if (parameters == null || parameters.Length < 7)
                    {
                        continue;
                    }

                    var centerInPart = new Point3d(
                        parameters[0] * 1000,
                        parameters[1] * 1000,
                        parameters[2] * 1000);
                    var centerInAssembly = centerInPart
                        .ConvertPointInPartToAssembly1000(swApp, sourceComponent);
                    result.Add(new HoleInfoOnFace
                    {
                        HoleType = ToolType_e.Circle,
                        dimD = Math.Round(parameters[6] * 2 * 1000, 2),
                        CenterPoint = centerInAssembly,
                        Plane3D = sourcePlaneInAssembly,
                        Vector = sourcePlaneInAssembly.Normal.Normalized
                    });
                }
                else if (circleEdges.Count == 2 && lineEdges == 2 && edges.Length == 4)
                {
                    var firstCircle = circleEdges[0].GetCurve() as Curve;
                    var secondCircle = circleEdges[1].GetCurve() as Curve;
                    var firstParameters = firstCircle?.CircleParams as double[];
                    var secondParameters = secondCircle?.CircleParams as double[];
                    if (firstParameters == null || secondParameters == null ||
                        firstParameters.Length < 7 || secondParameters.Length < 7)
                    {
                        continue;
                    }

                    var firstCenter = new Point3d(
                        firstParameters[0] * 1000,
                        firstParameters[1] * 1000,
                        firstParameters[2] * 1000)
                        .ConvertPointInPartToAssembly1000(swApp, sourceComponent);
                    var secondCenter = new Point3d(
                        secondParameters[0] * 1000,
                        secondParameters[1] * 1000,
                        secondParameters[2] * 1000)
                        .ConvertPointInPartToAssembly1000(swApp, sourceComponent);
                    result.Add(new HoleInfoOnFace
                    {
                        HoleType = ToolType_e.Slot,
                        // 与 BusbarTools1 一致：腰孔 dimD 记录半径，Cut 方法内部乘 2。
                        dimD = Math.Round(firstParameters[6] * 1000, 2),
                        dimX = firstCenter.DistanceTo(secondCenter),
                        CenterPoint = firstCenter,
                        CenterPoint2 = secondCenter,
                        Plane3D = sourcePlaneInAssembly,
                        Vector = sourcePlaneInAssembly.Normal.Normalized
                    });
                }
            }

            return result;
        }

        private static HoleInfoOnFace ConvertHoleToTargetPlane(
            HoleInfoOnFace sourceHole,
            Plane3d targetPlane,
            Vector3d targetNormal)
        {
            if (sourceHole == null || sourceHole.CenterPoint == null || targetPlane == null)
            {
                return null;
            }

            var result = new HoleInfoOnFace
            {
                HoleType = sourceHole.HoleType,
                dimD = sourceHole.dimD,
                dimX = sourceHole.dimX,
                dimY = sourceHole.dimY,
                Plane3D = targetPlane,
                Vector = targetNormal,
                CenterPoint = sourceHole.CenterPoint.ProjectionTo(targetPlane)
            };

            if (sourceHole.CenterPoint2 != null)
            {
                result.CenterPoint2 = sourceHole.CenterPoint2.ProjectionTo(targetPlane);
            }

            return result;
        }

        private static string BuildHoleKey(HoleInfoOnFace hole)
        {
            if (hole == null || hole.CenterPoint == null)
            {
                return string.Empty;
            }

            var secondPoint = hole.CenterPoint2 == null
                ? string.Empty
                : string.Format(
                    "|{0:0.###},{1:0.###},{2:0.###}",
                    hole.CenterPoint2.X,
                    hole.CenterPoint2.Y,
                    hole.CenterPoint2.Z);
            return string.Format(
                "{0}|{1:0.###},{2:0.###},{3:0.###}{4}|{5:0.###}",
                hole.HoleType,
                hole.CenterPoint.X,
                hole.CenterPoint.Y,
                hole.CenterPoint.Z,
                secondPoint,
                hole.dimD);
        }

        private static bool TryGetFacePlaneInAssembly(
            ISldWorks swApp,
            Component2 component,
            Face2 face,
            out Plane3d planeInAssembly)
        {
            planeInAssembly = null;
            try
            {
                var partPlane = face?.ToPlane3d();
                if (partPlane == null)
                {
                    return false;
                }

                planeInAssembly = partPlane.ConvertPlaneInPartToAssembly1000(swApp, component);
                return planeInAssembly != null;
            }
            catch
            {
                return false;
            }
        }

        private static List<Point3d> GetFaceBoundaryInAssembly(
            ISldWorks swApp,
            Component2 component,
            Face2 face)
        {
            var result = new List<Point3d>();
            try
            {
                var outerEdges = FaceUtility.GetFaceOuter(face);
                if (outerEdges == null)
                {
                    return result;
                }

                foreach (var edge in outerEdges)
                {
                    if (edge == null)
                    {
                        continue;
                    }

                    var startPoint = EdgeUtility.GetStart(edge)
                        .UnitX1000Copy()
                        .ConvertPointInPartToAssembly1000(swApp, component);
                    var endPoint = EdgeUtility.GetEnd(edge)
                        .UnitX1000Copy()
                        .ConvertPointInPartToAssembly1000(swApp, component);
                    AddDistinctBoundaryPoint(result, startPoint);
                    AddDistinctBoundaryPoint(result, endPoint);
                }
            }
            catch
            {
                result.Clear();
            }

            return result;
        }

        private static void AddDistinctBoundaryPoint(List<Point3d> points, Point3d point)
        {
            if (point != null && !points.Any(existing => existing.DistanceTo(point) <= 0.01))
            {
                points.Add(point);
            }
        }

        private static bool AreCoplanarAndParallel(Plane3d first, Plane3d second)
        {
            return first != null && second != null &&
                   first.Normal.Normalized.IsParallelToTol(second.Normal.Normalized, 0.1) &&
                   Math.Abs(first.Point.DistanceTo(second)) <= 2.0;
        }

        /// <summary>
        /// BusbarTools1 的 OverlappingArea/AreaModelHelper 思路：先投影到同一
        /// 参考平面，再检查点包含和边相交。这里保留为本项目自包含实现，避免跨项目引用。
        /// </summary>
        private static bool DoBusbarPolygonsOverlap(
            List<Point3d> firstPolygon,
            List<Point3d> secondPolygon)
        {
            if (firstPolygon == null || secondPolygon == null ||
                firstPolygon.Count < 3 || secondPolygon.Count < 3)
            {
                return false;
            }

            try
            {
                var referencePlane = CreatePolygonPlane(firstPolygon);
                if (referencePlane == null)
                {
                    return false;
                }

                var firstProjected = firstPolygon
                    .Select(point => point.ProjectionTo(referencePlane))
                    .ToList();
                var secondProjected = secondPolygon
                    .Select(point => point.ProjectionTo(referencePlane))
                    .ToList();
                var projectionAxis = GetProjectionAxis(referencePlane.Normal);

                if (firstProjected.Any(point => IsPointInBusbarPolygon(point, secondProjected, projectionAxis)) ||
                    secondProjected.Any(point => IsPointInBusbarPolygon(point, firstProjected, projectionAxis)))
                {
                    return true;
                }

                for (var firstIndex = 0; firstIndex < firstProjected.Count; firstIndex++)
                {
                    var firstSegment = new Segment3d(
                        firstProjected[firstIndex],
                        firstProjected[(firstIndex + 1) % firstProjected.Count]);
                    for (var secondIndex = 0; secondIndex < secondProjected.Count; secondIndex++)
                    {
                        var secondSegment = new Segment3d(
                            secondProjected[secondIndex],
                            secondProjected[(secondIndex + 1) % secondProjected.Count]);
                        if (DoBusbarSegmentsIntersect(
                                firstSegment,
                                secondSegment,
                                projectionAxis))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static Plane3d CreatePolygonPlane(List<Point3d> polygon)
        {
            for (var firstIndex = 0; firstIndex < polygon.Count - 2; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < polygon.Count - 1; secondIndex++)
                {
                    for (var thirdIndex = secondIndex + 1; thirdIndex < polygon.Count; thirdIndex++)
                    {
                        try
                        {
                            var plane = new Plane3d(
                                polygon[firstIndex],
                                polygon[secondIndex],
                                polygon[thirdIndex]);
                            if (plane.Normal.Norm > 0.0001)
                            {
                                return plane;
                            }
                        }
                        catch
                        {
                            // 继续寻找非共线的三个边界点。
                        }
                    }
                }
            }

            return null;
        }

        private static bool IsPointInBusbarPolygon(
            Point3d point,
            List<Point3d> polygon,
            int projectionAxis)
        {
            var intersections = 0;
            for (var index = 0; index < polygon.Count; index++)
            {
                var first = polygon[index];
                var second = polygon[(index + 1) % polygon.Count];
                var firstCoordinate = GetPolygonCoordinate(first, projectionAxis, true);
                var firstOtherCoordinate = GetPolygonCoordinate(first, projectionAxis, false);
                var secondCoordinate = GetPolygonCoordinate(second, projectionAxis, true);
                var secondOtherCoordinate = GetPolygonCoordinate(second, projectionAxis, false);
                var pointCoordinate = GetPolygonCoordinate(point, projectionAxis, true);
                var pointOtherCoordinate = GetPolygonCoordinate(point, projectionAxis, false);

                if (((firstCoordinate <= pointCoordinate && pointCoordinate < secondCoordinate) ||
                     (secondCoordinate <= pointCoordinate && pointCoordinate < firstCoordinate)) &&
                    pointOtherCoordinate <
                    (secondOtherCoordinate - firstOtherCoordinate) *
                    (pointCoordinate - firstCoordinate) /
                    (secondCoordinate - firstCoordinate) + firstOtherCoordinate)
                {
                    intersections++;
                }
            }

            return intersections % 2 == 1;
        }

        private static bool DoBusbarSegmentsIntersect(
            Segment3d first,
            Segment3d second,
            int projectionAxis)
        {
            var firstOrientation = GetBusbarOrientation(first.P1, first.P2, second.P1, projectionAxis);
            var secondOrientation = GetBusbarOrientation(first.P1, first.P2, second.P2, projectionAxis);
            var thirdOrientation = GetBusbarOrientation(second.P1, second.P2, first.P1, projectionAxis);
            var fourthOrientation = GetBusbarOrientation(second.P1, second.P2, first.P2, projectionAxis);
            if (firstOrientation != secondOrientation && thirdOrientation != fourthOrientation)
            {
                return true;
            }

            return (firstOrientation == 0 && IsBusbarPointOnSegment(first.P1, second.P1, first.P2, projectionAxis)) ||
                   (secondOrientation == 0 && IsBusbarPointOnSegment(first.P1, second.P2, first.P2, projectionAxis)) ||
                   (thirdOrientation == 0 && IsBusbarPointOnSegment(second.P1, first.P1, second.P2, projectionAxis)) ||
                   (fourthOrientation == 0 && IsBusbarPointOnSegment(second.P1, first.P2, second.P2, projectionAxis));
        }

        private static int GetBusbarOrientation(
            Point3d first,
            Point3d second,
            Point3d third,
            int projectionAxis)
        {
            var firstCoordinate = GetPolygonCoordinate(first, projectionAxis, true);
            var firstOtherCoordinate = GetPolygonCoordinate(first, projectionAxis, false);
            var secondCoordinate = GetPolygonCoordinate(second, projectionAxis, true);
            var secondOtherCoordinate = GetPolygonCoordinate(second, projectionAxis, false);
            var thirdCoordinate = GetPolygonCoordinate(third, projectionAxis, true);
            var thirdOtherCoordinate = GetPolygonCoordinate(third, projectionAxis, false);
            var value = (secondCoordinate - firstCoordinate) *
                        (thirdOtherCoordinate - firstOtherCoordinate) -
                        (secondOtherCoordinate - firstOtherCoordinate) *
                        (thirdCoordinate - firstCoordinate);
            if (Math.Abs(value) < 0.0001)
            {
                return 0;
            }

            return value > 0 ? 1 : 2;
        }

        private static bool IsBusbarPointOnSegment(
            Point3d first,
            Point3d point,
            Point3d second,
            int projectionAxis)
        {
            var pointCoordinate = GetPolygonCoordinate(point, projectionAxis, true);
            var pointOtherCoordinate = GetPolygonCoordinate(point, projectionAxis, false);
            return pointCoordinate <= Math.Max(
                       GetPolygonCoordinate(first, projectionAxis, true),
                       GetPolygonCoordinate(second, projectionAxis, true)) &&
                   pointCoordinate >= Math.Min(
                       GetPolygonCoordinate(first, projectionAxis, true),
                       GetPolygonCoordinate(second, projectionAxis, true)) &&
                   pointOtherCoordinate <= Math.Max(
                       GetPolygonCoordinate(first, projectionAxis, false),
                       GetPolygonCoordinate(second, projectionAxis, false)) &&
                   pointOtherCoordinate >= Math.Min(
                       GetPolygonCoordinate(first, projectionAxis, false),
                       GetPolygonCoordinate(second, projectionAxis, false));
        }

        private static int GetProjectionAxis(Vector3d normal)
        {
            var absX = Math.Abs(normal.X);
            var absY = Math.Abs(normal.Y);
            var absZ = Math.Abs(normal.Z);
            if (absX >= absY && absX >= absZ) return 0;
            if (absY >= absX && absY >= absZ) return 1;
            return 2;
        }

        private static double GetPolygonCoordinate(
            Point3d point,
            int projectionAxis,
            bool firstCoordinate)
        {
            if (projectionAxis == 0) return firstCoordinate ? point.Y : point.Z;
            if (projectionAxis == 1) return firstCoordinate ? point.X : point.Z;
            return firstCoordinate ? point.X : point.Y;
        }

        #endregion

        public static void DeleteAllHoleInfoOnFaces(Component2 comp2)
        {
            // 获取所有切除草图特征名称
            List<string> allSketch = comp2.IGetModelDoc().GetFeatureNamesByType("ProfileFeature").ToList();
            List<string> allRemove = new List<string>();

            var partDoc = comp2.IGetModelDoc() as PartDoc;

            if (partDoc == null) return;

            foreach (string sketch in allSketch)
            {
                if (sketch.Contains("铜排开孔") || sketch.Contains("绝缘子开孔") ||
                    sketch.Contains("穿墙套管铜排开孔"))
                {
                    allRemove.Add(sketch);
                }
            }

            comp2.IGetModelDoc().ClearSelection();
            foreach (var sketch in allRemove)
            {
                var feature = partDoc.FeatureByName(sketch) as Feature;

                feature.Select(true);
            }

            comp2.IGetModelDoc().EditDelete();
            comp2.IGetModelDoc().EditRebuild3();
        }

        public static void OpenAllBusbarHoles3(this ISldWorks swApp, Component2 BusbarAssComp, List<Component2> Insulations
, double BusbarWidth, double BusbuaThickness, int Num)
        {
            var swAssModel = swApp.IActiveDoc2;
            var swAssDoc = swAssModel as AssemblyDoc;
            //ModelViewReset modelViewReset = new ModelViewReset((SldWorks)swApp);
            //modelViewReset.SaveModelView();
            //swAssModel.LargeAssemblyMode = true;

            var busCompPath = BusbarAssComp.GetPathName();
            try
            {
                DateTime t = DateTime.Now;

                MatingFaceOpenHoleInfo OpenHoleInfo = DataHelper.GetMatingFaceOpenHole(BusbarWidth, BusbarWidth,
                    "水平", AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(),
                    AutoOpeningHoleDataModel.PartType_e.Busbar.GetDescription(), BusbarWidth,
                    BusbarWidth);

                var busComps = ((object[])swAssDoc.GetComponents(false)).Cast<Component2>()
                    .ToList();

                Insulations = busComps.Where(x =>
                    x != null && x.IGetModelDoc() != null &&
                    x.IGetModelDoc().GetType() == (int)swDocumentTypes_e.swDocPART &&
                    Path.GetFileNameWithoutExtension(x.GetPathName()).Contains("绝缘子")).ToList();

                busComps = busComps.Where(x =>
                    x != null && !string.IsNullOrWhiteSpace(x.GetPathName()) &&
                    x.IGetModelDoc()?.GetType() == (int)swDocumentTypes_e.swDocPART
                    && x.IGetModelDoc().GetModelType() >= PartType_e.Busbar).ToList();

                busComps = busComps
                    .Where(GeneratedBusbarSubassemblyIdentity
                        .IsUnderGeneratedSubassembly)
                    .ToList();

                busComps = busComps
                    .GroupBy(component => component.IGetModelDoc())
                    .Select(componentGroup => componentGroup
                        .OrderBy(component => component.Name2 ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderBy(component => component.GetPathName() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(component => component.Name2 ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var thisBusComps = ((object[])(BusbarAssComp.IGetModelDoc() as AssemblyDoc).GetComponents(false)).Cast<Component2>()
                    .ToList();

                busComps = busComps.Where(x => thisBusComps.Any(y => y.GetPathName() == x.GetPathName())).ToList();
                
                List<Tuple<string, List<HoleInfoOnFaceAndName>>> tuples = new List<Tuple<string, List<HoleInfoOnFaceAndName>>>();

                foreach (var thisComp in busComps)
                {
                    var compASegs = thisComp.GetSegmentsInAss(swApp);
                    if (compASegs.Count < 1)
                    {
                        continue;
                    }

                    var startP = compASegs[0].P1.Move(compASegs[0].Direction, BusbarWidth / 2.0);

                    var startFaceV = swApp
                        .GetFaceByObj(thisComp,
                            compASegs[0].GetMiddlePoint2().ConvertPointInAssemblyToPart1000(swApp, thisComp))
                        .ToPlane3d().Normal.ConvertVectorPartToAssembly(swApp, thisComp);

                    var endP = compASegs[compASegs.Count - 1].P2.Move(compASegs[compASegs.Count - 1].Direction.Negate(),
                        BusbarWidth / 2.0);

                    var endFaceV = swApp
                        .GetFaceByObj(thisComp,
                            compASegs[compASegs.Count - 1].GetMiddlePoint2()
                                .ConvertPointInAssemblyToPart1000(swApp, thisComp))
                        .ToPlane3d().Normal.ConvertVectorPartToAssembly(swApp, thisComp);

                    var compV = new Vector3d(0, 1, 0).ConvertVectorPartToAssembly(swApp, thisComp);

                    var MatingFHoleNames = new List<HoleInfoOnFaceAndName>();
                    MatingFHoleNames.Add(
                        OpenHoleInfo.GetHoleNameInAssUseMatingFaceOpenHoleInfo(startP, startFaceV, compV));

                    MatingFHoleNames.Add(
                        OpenHoleInfo.GetHoleNameInAssUseMatingFaceOpenHoleInfo(endP, endFaceV, compV));

                    foreach (var Insulation in Insulations)
                    {
                        PartInfo tempPartInfo = new PartInfo();

                        //基准配合面信息 零件信息
                        MatingFacePreviewUtility.GetMatingFaceInformationToSw(Insulation.IGetModelDoc(),
                                 ref tempPartInfo, (SldWorks)swApp);

                        if (tempPartInfo.SwPropertiesMatingFaces.Count == 1)
                        {
                            var skFeatureForPeiHe2 =
                                (Feature)(Insulation.IGetModelDoc() as PartDoc).FeatureByName(tempPartInfo
                                    .SwPropertiesMatingFaces[0].MatingFaceName);

                            if (skFeatureForPeiHe2 != null)
                            {
                                MatingFaceInfo matingFaceInfo2 = MatingFaceUtility.GetMatingFaceInfo((SldWorks)swApp,
                                    (Sketch)skFeatureForPeiHe2.GetSpecificFeature2());

                                if (matingFaceInfo2.IsCircle)
                                {
                                    var InsulationPoint =
                                        matingFaceInfo2.CenterPoint.ConvertPointInPartToAssembly1000(swApp, Insulation);

                                    var InsulationV =
                                        matingFaceInfo2.Normal.ConvertVectorPartToAssembly(swApp, Insulation);

                                    foreach (var thisSeg in compASegs)
                                    {
                                        var face = swApp.GetFaceByObj(thisComp, thisSeg.GetMiddlePoint2()
                                            .ConvertPointInAssemblyToPart1000(swApp, thisComp));

                                        var facePlane = face.ToPlane3d().ConvertPlaneInPartToAssembly1000(swApp, thisComp);

                                        if (facePlane.Normal.Normalized.IsParallelToTol(InsulationV.Normalized, 0.1))
                                        {
                                            if (InsulationPoint.DistanceTo(facePlane) >
                                                BusbuaThickness * (Num * 2 - 1) + 1)
                                            {
                                                continue;
                                            }

                                            var thisPoint = InsulationPoint.ProjectionTo(facePlane)
                                                .ConvertPointInAssemblyToPart1000(swApp, thisComp);

                                            if (thisPoint.JudgePointInFace(face))
                                            {
                                                HoleInfoOnFaceAndName holeName = new HoleInfoOnFaceAndName();
                                                var str = Guid.NewGuid().ToString().Substring(0, 6);
                                                holeName.featureName = $"绝缘子开孔-{str}";
                                                holeName.sketchName = $"绝缘子开孔草图-{str}";

                                                var HoleInfo = new HoleInfoOnFace();

                                                HoleInfo.Vector = InsulationV.Normalized;
                                                HoleInfo.Plane3D =
                                                    new Plane3d(InsulationPoint, InsulationV);

                                                HoleInfo.HoleType = ToolType_e.Circle;
                                                HoleInfo.dimD = 13.0;
                                                HoleInfo.CenterPoint = InsulationPoint.ProjectionTo(facePlane);

                                                holeName.thisHole = new List<HoleInfoOnFace>() { HoleInfo };

                                                MatingFHoleNames.Add(holeName);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    #region
                    
                    foreach (var holeInfo in MatingFHoleNames)
                    {
                        foreach (var hole in holeInfo.thisHole)
                        {
                            hole.CenterPoint = hole.CenterPoint.ConvertPointInAssemblyToPart1000(swApp, BusbarAssComp);
                            if (hole.CenterPoint2 != null)
                            {
                                hole.CenterPoint2 =
                                    hole.CenterPoint2.ConvertPointInAssemblyToPart1000(swApp, BusbarAssComp);
                            }
                            hole.Vector = hole.Vector.ConvertVectorAssemblyToPart(swApp, BusbarAssComp);
                            hole.Plane3D = hole.Plane3D.ConvertPlaneInAssemblyToPart1000(swApp, BusbarAssComp);
                        }
                    }

                    #endregion

                    tuples.Add(new Tuple<string, List<HoleInfoOnFaceAndName>>(thisComp.GetPathName(),
                        MatingFHoleNames));

                }


                var thisPath = swApp.IActiveDoc2.GetPathName();
                swApp.CloseDoc(thisPath);

                int err = 0;
                int warn = 0;
                //swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocASSEMBLY);
                var busAssModel = swApp.OpenDoc6(busCompPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);

                swApp.ActivateDoc(busCompPath);
                int index = 1;

                ModelViewReset modelViewReset = new ModelViewReset((SldWorks)swApp);
                modelViewReset.SaveModelView();

                var busbarBatchComponentCount = 0;
                try
                {


                    var busComps2 = ((object[])(busAssModel as AssemblyDoc).GetComponents(false)).Cast<Component2>()
                        .ToList();

                    busComps2 = busComps2.Where(x =>
                        x != null && !string.IsNullOrWhiteSpace(x.GetPathName()) &&
                        x.IGetModelDoc()?.GetType() == (int)swDocumentTypes_e.swDocPART
                        && x.IGetModelDoc().GetModelType() >= PartType_e.Busbar).ToList();

                    busComps2 = busComps2
                        .GroupBy(component => component.IGetModelDoc())
                        .Select(componentGroup => componentGroup
                            .OrderBy(component => component.Name2 ?? string.Empty,
                                StringComparer.OrdinalIgnoreCase)
                            .First())
                        .OrderBy(component => component.GetPathName() ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(component => component.Name2 ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    busbarBatchComponentCount = busComps2.Count;
                    
                    foreach (var thisComp in busComps2)
                    {
                        foreach (var tuple in tuples)
                        {
                            if (tuple.Item1 != thisComp.GetPathName())
                            {
                                continue;
                            }

                            DateTime s = DateTime.Now;
                            swApp.CutUseHoleInfoOnFaceAndName(thisComp, tuple.Item2);

                            index++;
                            DateTime s1 = DateTime.Now;
                            FaceToWallPreviewTrace.Info(
                                "铜排开孔-单个铜排完成",
                                "零件=" +
                                Path.GetFileNameWithoutExtension(thisComp.GetPathName()) +
                                "，耗时毫秒=" +
                                (s1 - s).TotalMilliseconds.ToString(
                                    "0",
                                    CultureInfo.InvariantCulture));

                        }
                    }
                }
                catch (Exception e)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排开孔-单个铜排处理失败",
                        e,
                        "零件批次=" + busbarBatchComponentCount.ToString(
                            CultureInfo.InvariantCulture));
                    throw;
                }
                finally
                {
                    modelViewReset.ResetModelView(null);
                }

                busAssModel.Save();

                swApp.CloseDoc(busCompPath);
                swAssModel = swApp.OpenDoc6(thisPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
                

                DateTime t1 = DateTime.Now;
                FaceToWallPreviewTrace.Info(
                    "铜排开孔-批处理完成",
                    "耗时毫秒=" +
                    (t1 - t).TotalMilliseconds.ToString(
                        "0",
                        CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                FaceToWallPreviewTrace.Error(
                    "铜排开孔-执行失败",
                    e);
                throw;
            }
            finally
            {
                //modelViewReset.ResetModelView(null);
                //swAssModel.LargeAssemblyMode = false;
            }
        }

        public static string PartFeatureName(PartDoc swPart, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Guid.NewGuid().ToString().Substring(6);
            }
            else
            {
                if (swPart.IFeatureByName(name) != null)
                {
                    return name + "-" + Guid.NewGuid().ToString().Substring(6);
                }
                else
                {
                    return name;
                }

            }
        }

        public static void CutUseHoleInfoOnFaceAndName(this ISldWorks swApp, Component2 insertComp,
    List<HoleInfoOnFaceAndName> allHoles)
        {
            ModelDoc2 swModel = swApp.IActiveDoc2;

            // 切换到编辑新零件状态，以便在其上创建草图
            // 先编辑零件A
            insertComp.Select(false);
            AssemblyDoc assemblyDoc = (AssemblyDoc)swModel;

            try
            {
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchInference, false);
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchAutomaticRelations, false);
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchInferFromModel, false);

                foreach (HoleInfoOnFaceAndName holes in allHoles)
                {
                    List<HoleInfoOnFace> holeInfoOnFaces = holes.thisHole;
                    //HoleInfoOnFace.CenterPoint 是在装配体坐标系下的坐标，需要转换到零件坐标系下

                    List<Face2> areaCompAFaces = new List<Face2>();
                    foreach (var thisHole in holeInfoOnFaces)
                    {
                        var thisFace = swApp.GetFaceByObj(insertComp,
                            thisHole.CenterPoint.ConvertPointInAssemblyToPart1000(swApp, insertComp), 0.1);

                        if (thisFace != null && !areaCompAFaces.Contains(thisFace))
                        {
                            areaCompAFaces.Add(thisFace);
                        }
                    }

                    //Point3d centerInPart =
                    //    holeInfoOnFaces.Select(x => x.CenterPointInPart).ToList().CalculateCenterPoint();

                    foreach (var thisAreaCompAFace in areaCompAFaces)
                    {
                        if (thisAreaCompAFace == null)
                        {
                            continue;
                        }

                        int nInfo = 0;
                        assemblyDoc.EditPart2(true, true, ref nInfo);

                        var areaCompAFace = (Face2)insertComp.GetCorresponding(thisAreaCompAFace);
                        // 选中这个面创建草图
                        FaceUtility.Select(areaCompAFace, false);
                        swModel.InsertSketch();

                        Sketch activeSketchInA = swModel.SketchManager.ActiveSketch;

                        if (activeSketchInA == null)
                        {
                            assemblyDoc.EditAssembly();
                            return;
                        }

                        SketchManager skMgr = swModel.SketchManager;
                        skMgr.AddToDB = true;
                        skMgr.AutoSolve = false;

                        Point3d coincidenceCenterInAsm =
                            holeInfoOnFaces.Select(x => x.CenterPoint).ToList().CalculateCenterPoint().UnitX0001Copy();

                        foreach (HoleInfoOnFace holeInfoOnFace in holeInfoOnFaces)
                        {
                            if (holeInfoOnFace == null) continue;

                            if (!holeInfoOnFace.CenterPoint.ConvertPointInAssemblyToPart1000(swApp, insertComp)
                                    .JudgePointInFace(areaCompAFace))
                            {
                                continue;
                            }

                            // 需要先投影
                            Plane3d planeAInAsm = BaseExt.ConvertPlaneInPart2Assembly(swApp,
                                areaCompAFace.ToPlane3d(), insertComp);
                            Point3d projectionPt = holeInfoOnFace.CenterPoint.ProjectionTo(planeAInAsm);

                            // 将装配体坐标系下的点，转换到零件坐标系下
                            Point3d centerInPartA = BaseExt.ConvertPointAssemblylToPart(swApp,
                                projectionPt.UnitX0001Copy(), insertComp);

                            // 将点转换到草图坐标系下
                            Point3d insCenterPointInSketch =
                                    centerInPartA.ConvertModelPointToSketchPoint((SldWorks)swApp, activeSketchInA).UnitX1000Copy();

                            ToolType_e toolTypeE = holeInfoOnFace.HoleType;
                            if (toolTypeE.Equals(ToolType_e.Circle))
                            {
                                // 圆孔
                                double tempR = holeInfoOnFace.dimD / 2000;

                                skMgr.CreateCircleByRadius(insCenterPointInSketch.X / 1000,
                                    insCenterPointInSketch.Y / 1000,
                                    0, tempR);

                            }
                            else if (toolTypeE.Equals(ToolType_e.Slot))
                            {
                                // 腰孔
                                Vector3d closestSegmentAVector3dInSketch;

                                MySegment closestSegmentA = GetClosestSegmentByCoincidenceAreaPoint(swApp, insertComp,
                                     coincidenceCenterInAsm);

                                if (closestSegmentA == null)
                                {
                                    closestSegmentA = GetClosestSegmentByCoincidenceAreaPoint(swApp, insertComp,
                                         holeInfoOnFace.CenterPoint.UnitX0001Copy());
                                }

                                if (closestSegmentA == null)
                                {
                                    continue;
                                }

                                Vector3d closestSegmentAVector3d = closestSegmentA.ToVector();
                                // 向量转草图内
                                closestSegmentAVector3dInSketch = BaseExt
                                    .ConvertVecotrInPartToSketch((SldWorks)swApp, closestSegmentAVector3d,
                                        activeSketchInA)
                                    .Normalized;

                                // 需要先投影
                                Point3d projectionPt2 = holeInfoOnFace.CenterPoint2.ProjectionTo(planeAInAsm);
                                // 将装配体坐标系下的点，转换到零件坐标系下
                                Point3d center2InPartA = BaseExt.ConvertPointAssemblylToPart(swApp,
                                    projectionPt2.UnitX0001Copy(), insertComp);
                                // 将点转换到草图坐标系下
                                Point3d insCenterPoint2InSketch = 
                                    center2InPartA.ConvertModelPointToSketchPoint((SldWorks)swApp, activeSketchInA).UnitX1000Copy();

                                Vector3d defaultVector3d = new Vector3d(insCenterPointInSketch, insCenterPoint2InSketch)
                                    .Normalized;

                                Point3d insCenterPointInSketchFinal = insCenterPointInSketch;
                                Point3d insCenterPoint2InSketchFinal = insCenterPoint2InSketch;

                                // 所有的腰孔，腰的方向都需要沿着排长的方向，所有需要做判断
                                if (closestSegmentAVector3dInSketch.IsNotParallelTo(defaultVector3d))
                                {
                                    Vector3d defaultVector3dMateStyle =
                                        defaultVector3d.Rotate(new Rotation(new Vector3d(0, 0, 1), Math.PI / 2));
                                    string verticalBendType = insertComp.IGetModelDoc()
                                        .GetCustomInfoValue(insertComp.ReferencedConfiguration, "铜排折弯类型");
                                    bool isVerticalBend = verticalBendType == "立弯";
                                    if (isVerticalBend)
                                    {
                                        // 如果是立弯，那么判断逻辑会有变化，应该转他们对应夹角的度数
                                        //double angleTo = defaultVector3d.AngleTo(closestSegmentAVector3dInSketch);
                                        double signedAngle = SignedAngle(defaultVector3d,
                                            closestSegmentAVector3dInSketch,
                                            new Vector3d(0, 0, 1));
                                        defaultVector3dMateStyle =
                                            defaultVector3d.Rotate(new Rotation(new Vector3d(0, 0, 1), signedAngle));
                                    }

                                    Segment3d segment3d =
                                        new Segment3d(insCenterPointInSketch, insCenterPoint2InSketch);
                                    Point3d middlePoint = segment3d.GetMiddlePoint2();
                                    double segment3dLength = segment3d.Length / 2;

                                    insCenterPointInSketchFinal =
                                        middlePoint.Move(defaultVector3dMateStyle, segment3dLength);
                                    insCenterPoint2InSketchFinal =
                                        middlePoint.Move(defaultVector3dMateStyle.Negate(), segment3dLength);
                                }

                                swModel.SketchManager.CreateSketchSlot(
                                    (int)swSketchSlotCreationType_e.swSketchSlotCreationType_line,
                                    (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter,
                                    holeInfoOnFace.dimD * 2 / 1000, insCenterPointInSketchFinal.X / 1000,
                                    insCenterPointInSketchFinal.Y / 1000, insCenterPointInSketchFinal.Z / 1000,
                                    insCenterPoint2InSketchFinal.X / 1000, insCenterPoint2InSketchFinal.Y / 1000,
                                    insCenterPoint2InSketchFinal.Z / 1000, 0, 0, 0, 1, false);
                            }
                        }

                        // 退出草图编辑，准备创建切除特征
                        activeSketchInA = swModel.SketchManager.ActiveSketch;

                        swModel.SketchManager.InsertSketch(false);

                        // 选中草图，调用切除特征创建贯通孔
                        string selectName = (activeSketchInA as Feature).GetNameForSelection(out string selectType);
                        swModel.SelectByID(selectName, selectType, 0, 0, 0);

                        FeatureManager swFeatureManager = swModel.FeatureManager;
                        Feature myFeatureInA = swFeatureManager.FeatureCut4(true, false, false, 0, 0, 0.01, 0.01, false,
                            false, false, false, 0, 0, false, false, false, false, true, true, true, true, true, false,
                            0, 0, false, false);

                        if (myFeatureInA != null)
                        {
                            IExtrudeFeatureData2 extrudeFeatureData2 = null;
                            try
                            {
                                extrudeFeatureData2 = (IExtrudeFeatureData2)myFeatureInA.GetDefinition();
                                extrudeFeatureData2.AccessSelections(swModel, insertComp);
                                // 当需要开四个孔，却只有三个孔在面上时，切除成型到下个面会失败，只能改成给定深度，而给定深度中有个参数是与厚度相等，在这个Data里，设置为true
                                extrudeFeatureData2.LinkToThickness = true;

                                myFeatureInA.ModifyDefinition(extrudeFeatureData2, swModel, insertComp);
                            }
                            finally
                            {
                                extrudeFeatureData2?.ReleaseSelectionAccess();
                            }
                        }

                        // 重命名草图和切除特征，与原铜排保持一致
                        (activeSketchInA as Feature).Name =
                            PartFeatureName(insertComp.IGetModelDoc() as PartDoc, holes.sketchName);
                        int activeSketchInAId = (activeSketchInA as Feature).GetID();

                        if (myFeatureInA != null)
                        {
                            myFeatureInA.Name =
                                PartFeatureName(insertComp.IGetModelDoc() as PartDoc, holes.featureName);

                            swModel.ClearSelection();
                            var SelectHoleFaces = ((object[])myFeatureInA.GetFaces()).Cast<Face2>().ToList();

                            MarkHoleHelper markHoleHelper = new MarkHoleHelper((SldWorks)swApp);

                            if (swModel is PartDoc)
                            {
                                //if (CurrentSettings.CurrentSettingsBase.FreeHoleNoYaHua)
                                if (holeInfoOnFaces[0].NotYahua)
                                {
                                    markHoleHelper.SetColorForHoles(swModel, SelectHoleFaces);
                                }
                                else
                                {
                                    ClearColorForHoles(swModel, swModel, SelectHoleFaces);
                                }

                            }
                            else if (swModel is AssemblyDoc)
                            {
                                //var component = swModel.ISelectionManager.GetSelectedObjectsComponent3(1, 1);
                                if (holeInfoOnFaces[0].NotYahua)
                                {
                                    markHoleHelper.SetColorForHoles(insertComp.IGetModelDoc(), SelectHoleFaces);
                                }
                                else
                                {
                                    ClearColorForHoles(swModel, insertComp.IGetModelDoc(), SelectHoleFaces);
                                }
                            }
                        }
                        else
                        {
                            // 切除失败时删除草图
                            swModel.DeleteSelection(false);
                        }

                        skMgr.AddToDB = false;
                        skMgr.AutoSolve = true;
                    }
                }
            }
            catch (Exception e)
            {
                FaceToWallPreviewTrace.Error(
                    "跨组件映射开孔失败",
                    e);
            }
            finally
            {
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchInference, true);
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchAutomaticRelations, true);
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSketchInferFromModel, true);

                assemblyDoc.EditAssembly();
            }
        }

        private static void ClearColorForHoles(ModelDoc2 swModel, ModelDoc2 tempModel, List<Face2> holeFaces)
        {

            SelectionMgr selMgr = (SelectionMgr)swModel.SelectionManager;

            foreach (var holeFace in holeFaces)
            {

                var ret = selMgr.SuspendSelectionList();

                var vModelProp = (double[])tempModel.MaterialPropertyValues;

                var bottomMaterialPro = (double[])holeFace.GetMaterialPropertyValues2(1, null);

                bottomMaterialPro[0] = vModelProp[0];
                bottomMaterialPro[1] = vModelProp[1];
                bottomMaterialPro[2] = vModelProp[2];
                bottomMaterialPro[3] = vModelProp[3];
                bottomMaterialPro[4] = vModelProp[4];
                bottomMaterialPro[5] = vModelProp[5];
                bottomMaterialPro[6] = vModelProp[6];
                bottomMaterialPro[7] = vModelProp[7];
                bottomMaterialPro[8] = vModelProp[8];

                holeFace.SetMaterialPropertyValues2(bottomMaterialPro, 1, null);

                bottomMaterialPro = null;

            }

            selMgr.ResumeSelectionList();

            swModel.EditRebuild3();

            swModel.ClearSelection();
        }

        /// <summary>
        /// 计算两个向量之间的有符号夹角（弧度），范围 (-PI, PI]
        /// </summary>
        /// <param name="from">起始向量</param>
        /// <param name="to">目标向量</param>
        /// <param name="axis">旋转轴/平面法向量（通常是 0,0,1）</param>
        /// <returns>旋转所需的弧度值</returns>
        private static double SignedAngle(Vector3d from, Vector3d to, Vector3d axis)
        {
            // 归一化向量，防止长度影响计算
            Vector3d v1 = from.Normalized;
            Vector3d v2 = to.Normalized;

            // 叉积：确定旋转方向
            // v1 x v2 的结果如果与 axis 同向，则为正（逆时针）；反向则为负（顺时针）
            Vector3d cross = v1.Cross(v2);

            // 点积：确定角度大小的余弦值
            double dot = v1.Dot(v2);

            // Math.Atan2(y, x) 是计算角度的神器
            // y = 叉积在轴上的投影长度 (带符号)
            // x = 点积
            double angle = Math.Atan2(cross.Dot(axis), dot);

            return angle;
        }

        /// <summary>
        /// 获取重合区域中，距离重合中心点最近的线段
        /// </summary>
        /// <param name="areaComp"></param>
        /// <param name="areaCompFace"></param>
        /// <param name="coincidenceAreaCoincidenceCenterInAsm"></param>
        /// <returns></returns>
        private static MySegment GetClosestSegmentByCoincidenceAreaPoint(ISldWorks swApp, Component2 thisComp,
            Point3d coincidenceAreaCoincidenceCenterInAsm)
        {
            try
            {
                var compASegs = thisComp.GetSegmentsInAss(swApp);

                var thisP = coincidenceAreaCoincidenceCenterInAsm.UnitX1000Copy()
                    .ConvertPointInAssemblyToPart1000(swApp, thisComp);

                var closestSeg = compASegs.OrderBy(x => Math.Round(thisP.DistanceTo(x), 6)).First();

                var closestSegment = new MySegment(closestSeg.P1, closestSeg.P2);

                return closestSegment;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// 计算一组 Point3d 点集的几何中心点（质心）
        /// </summary>
        /// <param name="listP">点集合（不能为空）</param>
        /// <returns>点集的中心点（全局坐标系）</returns>
        /// <exception cref="ArgumentNullException">点集合为空时抛出</exception>
        public static Point3d CalculateCenterPoint(this List<Point3d> listP)
        {
            // 边界校验：空列表直接抛异常
            if (listP == null || listP.Count == 0)
            {
                return null;
            }

            // 单点直接返回该点的副本（避免引用传递）
            if (listP.Count == 1)
            {
                return listP[0].Copy();
            }

            // 初始化坐标累加值
            double sumX = 0, sumY = 0, sumZ = 0;
            int pointCount = listP.Count;

            // 遍历所有点，累加全局坐标系下的 X/Y/Z 坐标
            foreach (Point3d p in listP)
            {
                // 统一转换到全局坐标系（关键：避免局部坐标系坐标混乱）
                Point3d pGlobal = p.ConvertToGlobal();
                sumX += pGlobal.X;
                sumY += pGlobal.Y;
                sumZ += pGlobal.Z;
            }

            // 计算平均值，生成中心点（全局坐标系）
            double centerX = sumX / pointCount;
            double centerY = sumY / pointCount;
            double centerZ = sumZ / pointCount;

            return new Point3d(centerX, centerY, centerZ, Coord3d.GlobalCS);
        }

        /// <summary>
        /// 去掉最后一个 "-" 
        /// </summary>
        /// <param name="original"></param>
        /// <returns></returns>
        public static string GetRemoveLast_String(string original)
        {
            // 找到最后一个 "-" 的索引位置
            int lastIndex = original.LastIndexOf('-');

            // 如果找到了 "-"，截取前面的部分；否则返回原字符串
            return lastIndex > -1 ? original.Substring(0, lastIndex) : original;
        }
    }
}
