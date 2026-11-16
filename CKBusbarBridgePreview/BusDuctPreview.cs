using AutoOpeningHoleDataModel;
using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 母线槽预览主类
    /// 负责基架、绝缘子、母排的SolidWorks三维预览生成
    /// </summary>
    public class BusDuctPreview
    {
        /// <summary>
        /// 面对面场景坐标转换基准组件名。
        /// </summary>
        public const string FaceToFaceReferenceComponentName = "B柜体-1";

        /// <summary>
        /// 面对墙场景坐标转换基准组件名。
        /// </summary>
        public const string FaceToWallReferenceComponentName = "柜体-1";

        private const double LegacyPhaseChangeDefaultBendAllowance = 20d;

        #region 私有成员变量
        /// <summary>
        /// SolidWorks应用程序对象
        /// </summary>
        private ISldWorks swApp;

        /// <summary>
        /// 当前活动的装配体文档
        /// </summary>
        private ModelDoc2 swAssModel;

        /// <summary>
        /// 装配体文档对象
        /// </summary>
        private AssemblyDoc swAssDoc;

        /// <summary>
        /// SolidWorks建模工具
        /// </summary>
        private IModeler swModeler;

        /// <summary>
        /// 坐标转换基准组件：预览体通过其 Transform 映射到装配体显示。
        /// 面对面为 B柜体-1，面对墙为柜体-1（见构造函数参数）。
        /// </summary>
        private Component2 compStandard;

        /// <summary>
        /// 绝缘子模型文件路径
        /// </summary>
        private string insulatorPath;
        #endregion

        /// <summary>
        /// 构造函数
        /// 初始化SolidWorks对象、获取标准组件、设置绝缘子模型路径
        /// </summary>
        /// <param name="swApp">SolidWorks应用程序对象</param>
        /// <param name="referenceComponentName">坐标转换基准组件名（面对面默认 <see cref="FaceToFaceReferenceComponentName"/>，面对墙为 <see cref="FaceToWallReferenceComponentName"/>）。</param>
        public BusDuctPreview(ISldWorks swApp, string referenceComponentName = FaceToFaceReferenceComponentName)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            if (string.IsNullOrWhiteSpace(referenceComponentName))
            {
                throw new ArgumentException("坐标转换基准组件名不能为空。", nameof(referenceComponentName));
            }

            this.swApp = swApp;
            swAssModel = swApp.IActiveDoc2;
            swAssDoc = swAssModel as AssemblyDoc;
            if (swAssDoc == null)
            {
                throw new InvalidOperationException("3D预览需要在装配体环境中执行，请先打开目标装配体。");
            }

            swModeler = swApp.IGetModeler();

            compStandard = swAssDoc.GetComponentByName(referenceComponentName);
            if (compStandard == null)
            {
                throw new InvalidOperationException(
                    $"未在装配体中找到坐标转换基准组件“{referenceComponentName}”，请确认当前模型已完成柜体驱动。");
            }

            insulatorPath = UseBendToPreviewFreeBusBar.RegDllPath() + $@"\swtemplates\insulator.dat";
        }

        /// <summary>草稿入口只接受已核验的组件实例，不重新按默认名称查找。</summary>
        public BusDuctPreview(ISldWorks application, Component2 referenceComponent)
        {
            swApp = application ?? throw new ArgumentNullException(nameof(application));
            compStandard = referenceComponent ?? throw new ArgumentNullException(nameof(referenceComponent));
            swAssModel = swApp.IActiveDoc2;
            swAssDoc = swAssModel as AssemblyDoc;
            if (swAssDoc == null) throw new InvalidOperationException("3D预览需要目标总装配体。");
            swModeler = swApp.IGetModeler();
            insulatorPath = UseBendToPreviewFreeBusBar.RegDllPath() + @"\swtemplates\insulator.dat";
        }

        public List<FreeDrawBody> BaseFramePreview(IEnumerable<DraftSupportSolid> solids)
            => PreviewDraftSupports(solids);

        // 草稿已包含逐套定位及实际高度，不能再经过旧数量均布或按dat模板固定高度覆盖。
        public List<FreeDrawBody> InsulatorPreview(IEnumerable<DraftSupportSolid> solids)
            => PreviewDraftSupports(solids);

        private List<FreeDrawBody> PreviewDraftSupports(IEnumerable<DraftSupportSolid> solids)
        {
            var result = new List<FreeDrawBody>();
            foreach (var group in solids.GroupBy(s => s.Round == null ? s.Id : s.RoundGroupId))
            {
                var solid = group.First();
                if (solid.Round != null)
                {
                    Body2 roundBody = null;
                    foreach (var segment in group)
                    {
                        var shape = segment.Round;
                        var axis = shape.End.Zip(shape.Start, (end, start) => (end - start) / shape.Length).ToArray();
                        var parameters = shape.Start.Select(v => v / 1000).Concat(axis)
                            .Concat(Math.Abs(shape.StartRadius - shape.EndRadius) < .000001
                                ? new[] { shape.StartRadius / 1000, shape.Length / 1000 }
                                : new[] { shape.StartRadius / 1000, shape.EndRadius / 1000, shape.Length / 1000 }).ToArray();
                        var part = (Body2)(parameters.Length == 8
                            ? swModeler.CreateBodyFromCyl(parameters) : swModeler.CreateBodyFromCone(parameters));
                        if (part == null) throw new InvalidOperationException("绝缘子回转截段创建失败：" + segment.Id);
                        if (roundBody == null) roundBody = part;
                        else
                        {
                            var merged = UseBendToPreviewFreeBusBar.Bady2Operationgs2(roundBody, part, "A");
                            if (merged.Count != 1) throw new InvalidOperationException("绝缘子回转截段未能连成单一本体：" + segment.Id);
                            roundBody = merged[0];
                        }
                    }
                    if (roundBody == null || !roundBody.ApplyTransform(compStandard.Transform.IInverse()))
                        throw new InvalidOperationException("绝缘子回转预览实体创建或定位失败：" + solid.Id);
                    result.Add(new FreeDrawBody { Body = roundBody, Component2 = compStandard,
                        Color = solid.Material == "rim" ? Color.DimGray : Color.Ivory, Name = solid.Id, CanSelect = 0, TempMater = DraftSupportSolid.PreviewTransparency });
                    continue;
                }
                var setting = PlanarBendPreviewAdapter.Create(solid.ToPrismSetting());
                var color = solid.Material == "insulator" ? Color.Ivory : solid.Material == "pad" ? Color.SteelBlue : Color.DimGray;
                var body = UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, setting, color, compStandard);
                if (body?.Body == null) throw new InvalidOperationException("支撑预览实体创建失败：" + solid.Id);
                body.Name = solid.Id;
                body.TempMater = DraftSupportSolid.PreviewTransparency;
                result.Add(body);
            }
            return result;
        }

        /// <summary>
        /// 生成基架三维预览
        /// </summary>
        /// <param name="busDuct">母线槽数据对象</param>
        /// <returns>预览实体列表</returns>
        public List<FreeDrawBody> BaseFramePreview(BusDuctData busDuct)
        {
            List<BaseFrameData> baseFrameDatas = busDuct.BasesFrames;
            List<FreeDrawBody> listBody = new List<FreeDrawBody>();

            foreach (BaseFrameData baseFrame in baseFrameDatas)
            {
                // 初始化折弯参数
                BendPreviewSetting bend = new BendPreviewSetting();
                bend.BusBarThickness = 3;
                bend.BarBendingRadius = 1;
                bend.BusBarWidth = baseFrame.Length;

                // 计算Z轴方向向量
                Vector3d ZVector;
                ZVector = baseFrame.XVector3d.Cross(baseFrame.YVector3d).Negate().Normalized;

                // 设置折弯方向向量
                bend.StartBusbarVector = ZVector;
                bend.WidthVector = ZVector.Cross(baseFrame.YVector3d).Negate();

                // 构建基架轮廓点集
                List<Point3d> listP = new List<Point3d>();
                listP.Add(baseFrame.CenterPoint.Move(ZVector.Negate(), baseFrame.Height / 2.0));
                listP.Add(baseFrame.CenterPoint.Move(ZVector.Negate(), baseFrame.Height / 2.0).Move(baseFrame.YVector3d, baseFrame.Width / 2.0));
                listP.Add(baseFrame.CenterPoint.Move(ZVector, baseFrame.Height / 2.0).Move(baseFrame.YVector3d, baseFrame.Width / 2.0));
                listP.Add(baseFrame.CenterPoint.Move(ZVector, baseFrame.Height / 2.0).Move(baseFrame.YVector3d.Negate(), baseFrame.Width / 2.0));
                listP.Add(baseFrame.CenterPoint.Move(ZVector.Negate(), baseFrame.Height / 2.0).Move(baseFrame.YVector3d.Negate(), baseFrame.Width / 2.0));
                listP.Add(baseFrame.CenterPoint.Move(ZVector.Negate(), baseFrame.Height / 2.0).Move(baseFrame.YVector3d.Negate(), 0.1));

                // 赋值轮廓点并计算
                bend.PointGather = listP;
                bend.SetPointGather();

                // 生成基架预览实体
                FreeDrawBody fdBody = UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend, Color.DimGray, compStandard);

                if (fdBody?.Body != null)
                {
                    listBody.Add(fdBody);
                }
            }

            return listBody;
        }

        /// <summary>
        /// 生成绝缘子三维预览
        /// </summary>
        /// <param name="busDuct">母线槽数据对象</param>
        /// <returns>预览实体列表</returns>
        public List<FreeDrawBody> InsulatorPreview(BusDuctData busDuct)
        {
            List<BaseFrameData> baseFrameDatas = busDuct.BasesFrames;

            // 校验绝缘子模型文件
            if (string.IsNullOrWhiteSpace(insulatorPath) || !File.Exists(insulatorPath))
            {
                return new List<FreeDrawBody>();
            }

            List<FreeDrawBody> listBody = new List<FreeDrawBody>();
            foreach (BaseFrameData baseFrame in baseFrameDatas)
            {
                // 无绝缘子则跳过
                if (baseFrame.InsulatorNum < 1)
                {
                    continue;
                }

                // 构建绝缘子方向向量
                List<Vector3d> Lvector = new List<Vector3d>()
                {
                    baseFrame.XVector3d,
                    baseFrame.XVector3d.Cross(baseFrame.YVector3d).Negate()
                };

                // 计算Z轴方向向量
                Vector3d zV = baseFrame.XVector3d.Cross(baseFrame.YVector3d).Negate();

                // 循环生成每个绝缘子
                for (int n = 0; n < baseFrame.InsulatorNum; n++)
                {
                    // 计算绝缘子X方向中心位置
                    Point3d LCenterP = baseFrame.CenterPoint.Move(baseFrame.XVector3d.Negate(),
                        baseFrame.Length / 2.0 - baseFrame.InsulatorDic[n]);

                    // 计算绝缘子最终三维坐标
                    Point3d centerP = LCenterP.Move(zV.Negate(), baseFrame.Height / 2.0 - 66.5)
                        .Move(baseFrame.YVector3d, baseFrame.Width / 2.0 - baseFrame.InsulatorLeftDic);

                    // 按相序数量生成绝缘子；相间距优先取该基架自身值（大基架为套管相间距）。
                    var phaseSpacing = baseFrame.InsulatorPhaseSpacing > 0
                        ? baseFrame.InsulatorPhaseSpacing
                        : busDuct.PhaseDis;
                    for (int i = 0; i < busDuct.PhaseNum; i++)
                    {
                        Point3d centerGD = centerP.Copy();
                        centerGD = centerGD.Move(baseFrame.YVector3d.Negate(), i * phaseSpacing);

                        // 坐标转换并插入绝缘子模型
                        Point3d point = centerGD.ConvertPointInAssemblyToPart1000(swApp, compStandard).UnitX0001Copy();
                        Body2 body = (Body2)swApp.InBody2(insulatorPath, point.ToArrayData(), Lvector);

                        // 封装预览实体
                        FreeDrawBody fdBody = new FreeDrawBody();
                        fdBody.Color = Color.White;
                        fdBody.Body = body;
                        fdBody.Component2 = compStandard;
                        fdBody.TempMater = 0.3;

                        if (fdBody?.Body != null)
                        {
                            listBody.Add(fdBody);
                        }
                    }
                }
            }

            return listBody;
        }

        /// <summary>
        /// 计算母排桥身的分段中心点，供预览和独立 Worker 输入适配共同使用。
        /// </summary>
        public static bool TryBuildPositionList(
            BusDuctData busDuct,
            double insulatorDistance,
            out List<Point3d> positionList,
            out string validationMessage)
        {
            positionList = new List<Point3d>();
            validationMessage = null;
            if (busDuct == null || busDuct.BasesFrames == null ||
                busDuct.BasesFrames.Count == 0 || busDuct.BendSetting == null ||
                busDuct.XVector == null || busDuct.YVector == null)
            {
                validationMessage = "缺少母排分段中心线所需的预览几何参数。";
                return false;
            }

            try
            {
                var baseFrames = busDuct.BasesFrames;
                var verticalDirection = busDuct.BendSetting.StartBusbarVector;
                if (verticalDirection == null || verticalDirection.Norm < 0.00000001)
                {
                    validationMessage = "母排预览竖向方向无效。";
                    return false;
                }

                var phaseCenterPlane = new Plane3d(
                    busDuct.BusbarStartPoint_B.Move(
                        busDuct.YVector,
                        busDuct.PhaseDis * (busDuct.PhaseNum - 1) / 2),
                    busDuct.YVector);
                var bridgeHeightPlane = new Plane3d(
                    baseFrames[0].CenterPoint.Move(
                        verticalDirection.Negate(),
                        baseFrames[0].Height / 2 - insulatorDistance),
                    verticalDirection);
                var bridgeCenterline =
                    phaseCenterPlane.IntersectionWith(bridgeHeightPlane) as Line3d;
                if (bridgeCenterline == null)
                {
                    validationMessage = "无法建立母排桥身中心线。";
                    return false;
                }

                var bridgeStartPoint = baseFrames[0].CenterPoint
                    .Move(
                        busDuct.XVector.Negate(),
                        baseFrames[0].Length / 2 - busDuct.StartSegmentation -
                        busDuct.BendSetting.BusBarWidth / 2)
                    .ProjectionTo(bridgeCenterline);
                var lastBaseFrame = baseFrames[baseFrames.Count - 1];
                var bridgeEndPoint = lastBaseFrame.CenterPoint
                    .Move(
                        busDuct.XVector,
                        lastBaseFrame.Length / 2 - busDuct.EndSegmentation -
                        busDuct.BendSetting.BusBarWidth / 2)
                    .ProjectionTo(bridgeCenterline);

                positionList.Add(bridgeStartPoint);
                foreach (var internalBreakpointPosition in
                         busDuct.OtherPositions ?? new List<double>())
                {
                    positionList.Add(
                        bridgeStartPoint.Move(
                            busDuct.XVector,
                            internalBreakpointPosition));
                }

                positionList.Add(bridgeEndPoint);
                return positionList.Count >= 2;
            }
            catch (Exception positionCalculationException)
            {
                positionList.Clear();
                validationMessage =
                    "计算母排分段中心线失败：" + positionCalculationException.Message;
                return false;
            }
        }

        /// <summary>
        /// 生成母排三维预览。
        /// 坐标在装配体空间（mm）计算：先求桥身中心线 <c>midLine</c> 与分段点列 <c>positionList</c>，
        /// 再按段生成 L 排搭接、直排/桥型及面对墙末段特殊折弯路径，
        /// 最后经 <see cref="compStandard"/> 变换显示。
        /// </summary>
        /// <param name="busDuct">母线槽预览数据（由 FrameDriverWindowVM 构建）。</param>
        /// <param name="insulatorDic">绝缘子参考高度（mm），用于桥身中心线与 L 排接入平面，默认 271.5。</param>
        /// <returns>预览实体列表。</returns>
        public List<FreeDrawBody> BusbarPreview(BusDuctData busDuct, double insulatorDic = 271.5)
        {
            List<FreeDrawBody> listBody = new List<FreeDrawBody>();
            List<BaseFrameData> baseF = busDuct.BasesFrames;
            try
            {
                IReadOnlyList<FaceToWallPhasePreviewData> faceToWallPhases = null;
                if (busDuct.IsFaceWall)
                {
                    faceToWallPhases = FaceToWallPhasePreviewValidator.ValidateAndOrder(
                        busDuct.FaceToWallPhasePreviews);
                }

                if (busDuct.IntChangePhase >= GetSegmentCount(busDuct))
                {
                    busDuct.IntChangePhase = -1;
                    busDuct.IntVBend = -1;
                }

                // Z 轴：铜排竖向（与 BusDuctData.BendSetting.StartBusbarVector 一致）。
                Vector3d ZVector = busDuct.BendSetting.StartBusbarVector;

                // 统一调用扫描器生成完整桥身中心线；后续所有铜排成员（含换相）
                // 均只能从这份 positionList 展开，避免预览入口再次复制一套断点算法。
                if (!TryBuildPositionList(
                        busDuct,
                        insulatorDic,
                        out var positionList,
                        out var positionValidationMessage))
                {
                    throw new InvalidOperationException(
                        positionValidationMessage ?? "无法建立桥身扫描中心线。");
                }

                var startP = positionList[0];
                var endP = positionList[positionList.Count - 1];

                // positionList 是整条桥身的扫描中心线。所有铜排路径都从同一组
                // 对称叠排层位展开，避免各分支重新定义一套单侧偏移。
                var stackMemberOffsets = GetCenteredStackMemberOffsets(busDuct);

                // 循环生成每一段母排（段索引 i 对应 positionList[i] ~ positionList[i+1]）
                bool bChangePhase = false;
                bool bVBend = false;
                for (int i = 0; i < positionList.Count - 1; i++)
                {
                    // 面对墙特殊末段已经按实际段长和墙端搭接一次性收口；
                    // 点列中其后的尾段只用于通用边界，不能再按普通直排重复绘制。
                    if (busDuct.IsFaceWall && busDuct.IntFaceWall > -1 && i > busDuct.IntFaceWall)
                    {
                        continue;
                    }

                    if (busDuct.IntChangePhase > -1 && i == busDuct.IntChangePhase + 1)
                    {
                        bChangePhase = true;
                    }

                    if (busDuct.IntVBend > -1 && i == busDuct.IntVBend + 1)
                    {
                        bVBend = true;
                    }

                    // 按相序生成
                    for (int l = 0; l < busDuct.PhaseNum; l++)
                    {
                        Color color = Color.Blue;
                        if (!bChangePhase)
                        {
                            color = UseBendToPreviewFreeBusBar.GetBusbarPhaseColor(busDuct.AllBusbarPhase_e[l]);
                        }
                        else
                        {
                            color = UseBendToPreviewFreeBusBar.GetBusbarPhaseColor(
                                busDuct.AllBusbarPhase_e[busDuct.PhaseNum - l - 1]);
                        }

                        List<Point3d> gdpositionList = positionList.ToList();

                        double d = busDuct.PhaseDis * l;

                        if (bVBend)
                        {
                            d = busDuct.PhaseDis * l + busDuct.VBendOffset;
                        }

                        // 当前相序平面：自 startP 沿 -Y 偏 d，法向 Y；后续点投影到该平面。
                        var thisPlane = new Plane3d(positionList[0].Move(busDuct.YVector.Negate(), d), busDuct.YVector);

                        // 按相序偏移位置
                        for (int c = 0; c < gdpositionList.Count; c++)
                        {
                            gdpositionList[c] = gdpositionList[c].ProjectionTo(thisPlane);
                        }

                        if (i == 0)
                        {
                            #region 第 0 段：B 柜 L 排搭接（面对墙时跳过 A 柜 L 排）

                            BendPreviewSetting Bend_B = busDuct.BendSetting.Copy();
                            Bend_B.StartBusbarVector = busDuct.XVector;
                            Bend_B.WidthVector = Bend_B.StartBusbarVector.Cross(ZVector).Negate();

                            Bend_B.PointGather.Clear();
                            var startBusbarP =
                                busDuct.BusbarStartPoint_B.Move(busDuct.XVector.Negate(), Bend_B.BusBarThickness);
                            Bend_B.PointGather.Add(startBusbarP);
                            startBusbarP = startBusbarP.Move(ZVector, busDuct.BusbarH_Section3_B);
                            Bend_B.PointGather.Add(startBusbarP);

                            //startBusbarP = startBusbarP.Move(ZVector, busDuct.BusbarH_Section2_B);
                            startBusbarP = startBusbarP.Move(busDuct.XVector.Negate(),
                                busDuct.BusbarTotalLength_B - busDuct.BusbarBendDis_B);

                            startBusbarP = startBusbarP.ProjectionTo(new Plane3d(baseF[0].CenterPoint.Move(
                                ZVector.Negate(), baseF[0].Height / 2.0 - insulatorDic -
                                                  GetStackEnvelopeThickness(busDuct) +
                                                  busDuct.BusbarH_Section1_B), ZVector));

                            Bend_B.PointGather.Add(startBusbarP);
                            startBusbarP = startBusbarP.Move(ZVector, busDuct.BusbarH_Section1_B);
                            Bend_B.PointGather.Add(startBusbarP);
                            startBusbarP = startBusbarP.ProjectionTo(new Plane3d(
                                startP.Move(busDuct.XVector, Bend_B.BusBarWidth / 2.0),
                                busDuct.XVector));
                            Bend_B.PointGather.Add(startBusbarP);

                            for (int j = 0; j < Bend_B.PointGather.Count; j++)
                            {
                                Bend_B.PointGather[j] = Bend_B.PointGather[j].ProjectionTo(thisPlane);
                            }

                            Bend_B.SetPointGather();
                            // 生成母排预览实体
                            FreeDrawBody fdBody2 =
                                UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, Bend_B, color,
                                    compStandard);
                            listBody.Add(fdBody2);

                            for (int j = 1; j < GetStackMemberCount(busDuct); j++)
                            {
                                Bend_B.GetPointGather();

                                Bend_B.PointGather = Bend_B.PointGather.OffsetListPoint(Bend_B.StartBusbarVector,
                                    GetStackMemberPitch(busDuct),
                                    out _);

                                Bend_B.SetPointGather();

                                FreeDrawBody gdfdBody =
                                    UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, Bend_B, color,
                                        compStandard);
                                listBody.Add(gdfdBody);
                            }

                            if (!busDuct.IsFaceWall)
                            {
                                // A 柜 L 排：从 A_BDis 偏移方向折弯，末端投影到 endP 侧桥身起点。
                                BendPreviewSetting Bend_A = busDuct.BendSetting.Copy();
                                Bend_A.StartBusbarVector = busDuct.XVector.Negate();
                                Bend_A.WidthVector = Bend_A.StartBusbarVector.Cross(ZVector).Negate();

                                Bend_A.PointGather.Clear();
                                var endBusbarP = busDuct.BusbarStartPoint_B.Move(busDuct.XVector,
                                    busDuct.A_BDis - Bend_A.BusBarThickness);
                                Bend_A.PointGather.Add(endBusbarP);

                                endBusbarP = endBusbarP.Move(ZVector, busDuct.BusbarH_Section3_A);
                                Bend_A.PointGather.Add(endBusbarP);

                                endBusbarP = endBusbarP.Move(busDuct.XVector,
                                    busDuct.BusbarTotalLength_A - busDuct.BusbarBendDis_A);

                                endBusbarP = endBusbarP.ProjectionTo(new Plane3d(baseF[0].CenterPoint.Move(
                                    ZVector.Negate(),
                                    baseF[0].Height / 2.0 - insulatorDic -
                                    GetStackEnvelopeThickness(busDuct) +
                                    busDuct.BusbarH_Section1_B), ZVector));

                                var offset = busDuct.OtherPositions.Count % 2 == 1
                                    ? busDuct.BendSetting.BusBarThickness
                                    : 0;

                                endBusbarP = endBusbarP.Move(ZVector.Negate(), offset);

                                Bend_A.PointGather.Add(endBusbarP);
                                endBusbarP = endBusbarP.Move(ZVector, busDuct.BusbarH_Section1_A);
                                Bend_A.PointGather.Add(endBusbarP);
                                endBusbarP = endBusbarP.ProjectionTo(new Plane3d(
                                    endP.Move(busDuct.XVector.Negate(), Bend_A.BusBarWidth / 2.0),
                                    busDuct.XVector));
                                Bend_A.PointGather.Add(endBusbarP);

                                for (int j = 0; j < Bend_A.PointGather.Count; j++)
                                {
                                    Bend_A.PointGather[j] = Bend_A.PointGather[j].ProjectionTo(thisPlane);
                                }

                                Bend_A.SetPointGather();
                                // 生成母排预览实体
                                FreeDrawBody fdBody3 =
                                    UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, Bend_A, color,
                                        compStandard);
                                listBody.Add(fdBody3);

                                for (int j = 1; j < GetStackMemberCount(busDuct); j++)
                                {
                                    Bend_A.GetPointGather();

                                    Bend_A.PointGather = Bend_A.PointGather.OffsetListPoint(Bend_A.StartBusbarVector,
                                        GetStackMemberPitch(busDuct),
                                        out _);

                                    Bend_A.SetPointGather();

                                    FreeDrawBody gdfdBody =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, Bend_A, color,
                                            compStandard);
                                    listBody.Add(gdfdBody);
                                }
                            }

                            #endregion
                        }

                        if (busDuct.IntChangePhase > -1 &&
                            i == busDuct.IntChangePhase)
                        {
                            // 换相中间组先按扫描得到的叠排总包络建立一份完整中心线，
                            // 再沿同一排厚方向拆分全部叠排成员；禁止在成员循环内
                            // 重新按单根铜排拼接中间路径。
                            var phaseChangeStackEnvelopeThickness =
                                GetStackEnvelopeThickness(busDuct);
                            var phaseChangeCenterlinePaths =
                                CreateLegacyPhaseChangeCenterlinePaths(
                                    busDuct,
                                    l,
                                    gdpositionList[i],
                                    ZVector,
                                    phaseChangeStackEnvelopeThickness,
                                    GetStackMemberThickness(busDuct));
                            foreach (var stackMemberOffset in stackMemberOffsets)
                            {
                                foreach (var phaseChangeCenterlinePath in
                                    phaseChangeCenterlinePaths)
                                {
                                    var stackedPhaseChangePath =
                                        SplitLegacyStackEnvelopePhasePath(
                                            phaseChangeCenterlinePath,
                                            busDuct,
                                            stackMemberOffset,
                                            phaseChangeStackEnvelopeThickness,
                                            false);
                                    stackedPhaseChangePath.SetPointGather();
                                    var stackedPhaseChangeBody =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody(
                                            (SldWorks)swApp,
                                            stackedPhaseChangePath,
                                            color,
                                            compStandard);
                                    listBody.Add(stackedPhaseChangeBody);
                                }
                            }

                            continue;
                        }

                        if (busDuct.IntVBend > -1 &&
                            i == busDuct.IntVBend &&
                            Math.Abs(busDuct.VBendOffset - 0) > 0.9)
                        {
                            // 立弯先按叠排总包络厚度构造一条路径，再拆成单根排，
                            // 避免每根铜排独立计算立弯后出现层位和弯长不一致。
                            var stackEnvelopeVerticalBend =
                                CreateLegacyStackEnvelopeVerticalBend(
                                    busDuct,
                                    gdpositionList[i],
                                    gdpositionList[i + 1],
                                    ZVector,
                                    busDuct.VBendOffset);
                            foreach (var stackMemberOffset in stackMemberOffsets)
                            {
                                var splitVerticalBend =
                                    SplitLegacyStackEnvelopeVerticalBend(
                                        stackEnvelopeVerticalBend,
                                        busDuct,
                                        stackMemberOffset);
                                splitVerticalBend.SetPointGather();
                                var splitVerticalBendBody =
                                    UseBendToPreviewFreeBusBar.PreviewBendBody(
                                        (SldWorks)swApp,
                                        splitVerticalBend,
                                        color,
                                        compStandard);
                                listBody.Add(splitVerticalBendBody);
                            }

                            continue;
                        }

                        // 先使用 positionList 扫描出的完整中心线，再按成员索引展开叠排。
                        // 同一根铜排在所有分段使用同一层位，禁止旧版按奇偶段跳变半排厚度。
                        for (int j = 0; j < stackMemberOffsets.Count; j++)
                        {
                            BendPreviewSetting bend = busDuct.BendSetting.Copy();
                            var memberOffset = stackMemberOffsets[j];

                            // 段内直排端点：P1/P2 均直接取该成员的中心线层位，
                            // 由完整扫描中心线保证跨分段接缝连续。
                            Point3d P1 = gdpositionList[i].Move(
                                    ZVector,
                                    memberOffset)
                                .Move(busDuct.XVector.Negate(), bend.BusBarWidth / 2.0);

                            Point3d P2 = gdpositionList[i + 1].Move(
                                    ZVector,
                                    memberOffset)
                                .Move(busDuct.XVector, bend.BusBarWidth / 2.0);

                            // 设置母排轮廓点
                            bend.PointGather.Clear();
                            bend.PointGather.Add(P1);

                            Point3d lP = null;
                            if (busDuct.IntChangePhase > -1 && i == busDuct.IntChangePhase)
                            {
                                if (l == 0)
                                {
                                    bend.PointGather.Add(P1.Move(busDuct.XVector,
                                        busDuct.A_Length1 + GetReverseStackStagger(busDuct, j)));

                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.PointGather.Add(lP.Move(ZVector, busDuct.A_Height1));

                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.PointGather.Add(lP.Move(busDuct.XVector,
                                        busDuct.A_Length2 - GetReverseStackStagger(busDuct, j)));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.SetPointGather();
                                    // 生成母排预览实体
                                    FreeDrawBody fdBody =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend, color,
                                            compStandard);
                                    listBody.Add(fdBody);

                                    var bend2 = bend.Copy();
                                    bend2.WidthVector = busDuct.YVector.Cross(ZVector).Negate();
                                    bend2.PointGather.Clear();
                                    bend2.PointGather.Add(lP
                                        .Move(busDuct.XVector.Negate(), bend2.BusBarWidth / 2.0)
                                        .Move(busDuct.YVector, bend2.BusBarWidth / 2.0)
                                        .Move(ZVector, bend2.BusBarThickness));
                                    lP = bend2.PointGather[bend2.PointGather.Count - 1].Copy();
                                    bend2.PointGather.Add(lP.Move(busDuct.YVector.Negate(),
                                        busDuct.PhaseDis * 2 + bend2.BusBarWidth));
                                    lP = bend2.PointGather[bend2.PointGather.Count - 1].Copy();
                                    bend2.SetPointGather();
                                    // 生成母排预览实体
                                    var fdBody2 =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend2, color,
                                            compStandard);
                                    listBody.Add(fdBody2);

                                    var bend3 = bend.Copy();
                                    bend3.PointGather.Clear();
                                    bend3.PointGather.Add(lP
                                        .Move(busDuct.XVector.Negate(), bend3.BusBarWidth / 2.0)
                                        .Move(busDuct.YVector, bend3.BusBarWidth / 2.0)
                                        .Move(ZVector.Negate(), bend3.BusBarThickness));
                                    lP = bend3.PointGather[bend3.PointGather.Count - 1].Copy();
                                    bend3.PointGather.Add(lP.Move(busDuct.XVector,
                                         busDuct.A_Length3 - GetReverseStackStagger(busDuct, j)));
                                    lP = bend3.PointGather[bend3.PointGather.Count - 1].Copy();
                                    bend3.PointGather.Add(lP.Move(ZVector.Negate(),
                                        busDuct.A_Height1 - bend3.BusBarThickness));
                                    lP = bend3.PointGather[bend3.PointGather.Count - 1].Copy();
                                    bend3.PointGather.Add(lP.Move(busDuct.XVector,
                                         busDuct.A_Length4 + GetReverseStackStagger(busDuct, j)));

                                    bend3.SetPointGather();
                                    // 生成母排预览实体
                                    var fdBody3 =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend3, color,
                                            compStandard);
                                    listBody.Add(fdBody3);
                                }
                                else if (l == 1)
                                {
                                    bend.PointGather.Add(P1.Move(busDuct.XVector,
                                         busDuct.B_Length1 + GetReverseStackStagger(busDuct, j)));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.PointGather.Add(lP.Move(ZVector, busDuct.B_Height1));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                     bend.PointGather.Add(lP.Move(busDuct.XVector,
                                         (busDuct.A_Length1 + busDuct.A_Length2 + busDuct.A_Length3
                                         - busDuct.B_Length1 - bend.BusBarWidth)
                                         - 2d * GetReverseStackStagger(busDuct, j)));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.PointGather.Add(lP.Move(ZVector.Negate(),
                                        busDuct.B_Height1 - bend.BusBarThickness));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.PointGather.Add(lP.Move(busDuct.XVector,
                                         busDuct.A_Length4 + GetReverseStackStagger(busDuct, j)));

                                    bend.SetPointGather();
                                    // 生成母排预览实体
                                    FreeDrawBody fdBody =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend, color,
                                            compStandard);
                                    listBody.Add(fdBody);
                                }
                                else if (l == 2)
                                {
                                    bend.PointGather.Add(P1.Move(busDuct.XVector, busDuct.C_Length1));
                                    lP = bend.PointGather[bend.PointGather.Count - 1].Copy();
                                    bend.SetPointGather();
                                    // 生成母排预览实体
                                    FreeDrawBody fdBody =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend, color,
                                            compStandard);
                                    listBody.Add(fdBody);

                                    var bend2 = bend.Copy();
                                    bend2.WidthVector = busDuct.YVector.Cross(ZVector).Negate();
                                    bend2.PointGather.Clear();
                                    bend2.PointGather.Add(lP
                                        .Move(busDuct.XVector.Negate(), bend2.BusBarWidth / 2.0)
                                        .Move(busDuct.YVector.Negate(), bend2.BusBarWidth / 2.0)
                                        .Move(ZVector, bend2.BusBarThickness));
                                    lP = bend2.PointGather[bend2.PointGather.Count - 1].Copy();
                                    bend2.PointGather.Add(lP.Move(busDuct.YVector,
                                        busDuct.PhaseDis * 2 + bend2.BusBarWidth));
                                    lP = bend2.PointGather[bend2.PointGather.Count - 1].Copy();
                                    bend2.SetPointGather();
                                    // 生成母排预览实体
                                    var fdBody2 =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend2, color,
                                            compStandard);
                                    listBody.Add(fdBody2);

                                    var bend3 = bend.Copy();
                                    bend3.PointGather.Clear();
                                    bend3.PointGather.Add(lP
                                        .Move(busDuct.XVector.Negate(), bend3.BusBarWidth / 2.0)
                                        .Move(busDuct.YVector.Negate(), bend3.BusBarWidth / 2.0)
                                        .Move(ZVector.Negate(), bend3.BusBarThickness));
                                    lP = bend3.PointGather[bend3.PointGather.Count - 1].Copy();
                                    bend3.PointGather.Add(lP.Move(busDuct.XVector,
                                        busDuct.A_Length1 + busDuct.A_Length2 + busDuct.A_Length3 + busDuct.A_Length4 -
                                        busDuct.C_Length1));

                                    bend3.SetPointGather();
                                    // 生成母排预览实体
                                    var fdBody3 =
                                        UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend3, color,
                                            compStandard);
                                    listBody.Add(fdBody3);
                                }
                            }
                            else if (busDuct.IntVBend > -1 && i == busDuct.IntVBend &&
                                     Math.Abs(busDuct.VBendOffset - 0) > 0.9)
                            {
                                var VBend = bend.Copy();
                                VBend.BusBarThickness = bend.BusBarWidth;
                                VBend.BusBarWidth = bend.BusBarThickness;
                                VBend.PointGather.Clear();
                                VBend.StartBusbarVector = busDuct.YVector;
                                VBend.WidthVector = busDuct.XVector.Cross(VBend.StartBusbarVector).Negate();
                                VBend.BarBendingRadius = busDuct.VBendRadius == 0
                                    ? bend.BusBarWidth / 4.0
                                    : busDuct.VBendRadius;

                                var vbSPoint = P1.Move(ZVector, bend.BusBarThickness / 2.0)
                                    .Move(busDuct.YVector.Negate(), bend.BusBarWidth / 2.0);
                                VBend.PointGather.Add(vbSPoint);
                                VBend.PointGather.Add(vbSPoint.Move(busDuct.XVector,
                                    bend.BusBarWidth + busDuct.BendLength));

                                var VP2 = P2.Move(ZVector, bend.BusBarThickness / 2.0)
                                    .Move(busDuct.YVector.Negate(),
                                        bend.BusBarWidth / 2.0 + busDuct.VBendOffset);

                                VBend.PointGather.Add(VP2.Move(busDuct.XVector.Negate(),
                                    bend.BusBarWidth + busDuct.BendLength));
                                VBend.PointGather.Add(VP2);

                                VBend.SetPointGather();

                                // 生成母排预览实体
                                FreeDrawBody fdBody =
                                    UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, VBend, color,
                                        compStandard);
                                listBody.Add(fdBody);

                            }

                            #region 面对墙末段（i == IntFaceWall）：A/B/C 逐相当前模板几何

                            else if (busDuct.IsFaceWall && busDuct.IntFaceWall > -1 && i == busDuct.IntFaceWall)
                            {
                                if (faceToWallPhases != null)
                                {
                                    var phasePreviewData = faceToWallPhases[l];
                                    var useStackEnvelopePhaseGeometry =
                                        phasePreviewData.TemplateKind ==
                                            FaceToWallPreviewTemplateKind.VerticalBend ||
                                        phasePreviewData.TemplateKind ==
                                            FaceToWallPreviewTemplateKind.MilledBar;
                                    if (useStackEnvelopePhaseGeometry)
                                    {
                                        if (j != 0)
                                        {
                                            // 特殊路径已经在第一个成员循环中按总厚度
                                            // 展开全部成员，后续成员不能再次生成。
                                            continue;
                                        }

                                        // 面对墙立弯/铣排同样先以叠排总厚度生成路径，
                                        // 再把路径沿局部排厚方向拆成单根铜排。
                                        var stackEnvelopeSource =
                                            busDuct.BendSetting.Copy();
                                        var stackEnvelopeThickness =
                                            GetStackEnvelopeThickness(busDuct);
                                        stackEnvelopeSource.BusBarThickness =
                                            stackEnvelopeThickness;
                                        var phaseCenterP1 =
                                            gdpositionList[i].Move(
                                                busDuct.XVector.Negate(),
                                                stackEnvelopeSource.BusBarWidth / 2.0);
                                        var phaseCenterP2 =
                                            gdpositionList[i + 1].Move(
                                                busDuct.XVector,
                                                stackEnvelopeSource.BusBarWidth / 2.0);
                                        var stackEnvelopePreviewSettings =
                                            FaceToWallPhasePreviewPathBuilder.Build(
                                                phasePreviewData,
                                                phaseCenterP1,
                                                phaseCenterP2,
                                                busDuct.XVector,
                                                busDuct.YVector,
                                                ZVector,
                                                stackEnvelopeSource);
                                        for (var stackMemberIndex = 0;
                                            stackMemberIndex < stackMemberOffsets.Count;
                                            stackMemberIndex++)
                                        {
                                            var stackMemberOffset =
                                                stackMemberOffsets[stackMemberIndex];
                                            for (var stackEnvelopePathIndex = 0;
                                                stackEnvelopePathIndex <
                                                stackEnvelopePreviewSettings.Count;
                                                stackEnvelopePathIndex++)
                                            {
                                                var splitPhasePath =
                                                    SplitLegacyStackEnvelopePhasePath(
                                                        stackEnvelopePreviewSettings[
                                                            stackEnvelopePathIndex],
                                                        busDuct,
                                                        stackMemberOffset,
                                                        stackEnvelopeThickness,
                                                        phasePreviewData.TemplateKind ==
                                                            FaceToWallPreviewTemplateKind.VerticalBend &&
                                                        stackEnvelopePathIndex == 0);
                                                splitPhasePath.SetPointGather();
                                                var splitPhaseBody =
                                                    UseBendToPreviewFreeBusBar.PreviewBendBody(
                                                        (SldWorks)swApp,
                                                        splitPhasePath,
                                                        color,
                                                        compStandard);
                                                listBody.Add(splitPhaseBody);
                                            }
                                        }

                                        continue;
                                    }

                                    var previewSettings = FaceToWallPhasePreviewPathBuilder.Build(
                                        faceToWallPhases[l],
                                        P1,
                                        P2,
                                        busDuct.XVector,
                                        busDuct.YVector,
                                        ZVector,
                                        bend);
                                    for (var previewPathIndex = 0;
                                         previewPathIndex < previewSettings.Count;
                                         previewPathIndex++)
                                    {
                                        var previewSetting = previewSettings[previewPathIndex];
                                        var currentPhaseBody =
                                            UseBendToPreviewFreeBusBar.PreviewBendBody(
                                                (SldWorks)swApp,
                                                previewSetting,
                                                color,
                                                compStandard);
                                        if (currentPhaseBody == null)
                                        {
                                            throw new InvalidOperationException(
                                                string.Format(
                                                    "{0}相面对墙铜排预览体生成失败。",
                                                    l == 0 ? "A" : l == 1 ? "B" : "C")
                                                + string.Format(
                                                    "模板：{0}，路径：{1}/{2}。",
                                                    faceToWallPhases[l].TemplateKind,
                                                    previewPathIndex + 1,
                                                    previewSettings.Count));
                                        }

                                        listBody.Add(currentPhaseBody);
                                    }

                                    continue;
                                }

                                #region 两侧反向（不换相）

                                var thisP1 = P1.Copy();
                                if (i % 2 == 1)
                                {
                                    thisP1 = P1.Move(ZVector.Negate(), bend.BusBarThickness);
                                }

                                if (l == 1)
                                {
                                    if (busDuct.Type面对墙类型 == 面对墙类型.左立弯 || busDuct.Type面对墙类型 == 面对墙类型.右立弯)
                                    {
                                        var VBend = bend.Copy();
                                        VBend.BusBarThickness = bend.BusBarWidth;
                                        VBend.BusBarWidth = bend.BusBarThickness;
                                        VBend.PointGather.Clear();
                                        VBend.StartBusbarVector = busDuct.YVector;
                                        VBend.WidthVector = busDuct.XVector.Cross(VBend.StartBusbarVector).Negate();
                                        VBend.BarBendingRadius = busDuct.VBendRadius == 0
                                            ? bend.BusBarWidth / 4.0
                                            : busDuct.VBendRadius;

                                        var vbSPoint = thisP1.Move(ZVector, bend.BusBarThickness / 2.0)
                                            .Move(busDuct.YVector.Negate(), bend.BusBarWidth / 2.0);
                                        VBend.PointGather.Add(vbSPoint);

                                        var lastP = vbSPoint.Move(busDuct.XVector,
                                            bend.BusBarWidth + busDuct.B相偏移第一段长度尺寸);
                                        VBend.PointGather.Add(lastP);

                                        var PY = busDuct.BendSetting.BusBarThickness +
                                                 busDuct.BendSetting.BarBendingRadius + 1;

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.AC相第一节直排长度 + busDuct.AC相折弯排第一段长度 -
                                            busDuct.BendSetting.BusBarWidth -
                                            busDuct.B相偏移第一段长度尺寸 - busDuct.B相偏移第二段长度尺寸 - PY);

                                        lastP = lastP.Move(
                                            busDuct.Type面对墙类型 == 面对墙类型.左立弯
                                                ? busDuct.YVector.Negate()
                                                : busDuct.YVector, busDuct.偏移尺寸);
                                        VBend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.B相偏移第二段长度尺寸 - PY -
                                            GetForwardStackStagger(busDuct, j));

                                        VBend.PointGather.Add(lastP);
                                        VBend.SetPointGather();

                                        // 生成母排预览实体
                                        FreeDrawBody fdBody =
                                            UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, VBend,
                                                color,
                                                compStandard);
                                        listBody.Add(fdBody);

                                        bend.PointGather.Clear();
                                        lastP = lastP.Move(ZVector.Negate(),
                                                busDuct.BendSetting.BusBarThickness / 2.0)
                                            .Move(busDuct.YVector, busDuct.BendSetting.BusBarWidth / 2.0);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector, PY + 3);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(ZVector, busDuct.三相折弯高度);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector, busDuct.三相折弯排第二段长度
                                                                            - GetReverseStackStaggerWithOneLayer(busDuct, j));
                                        bend.PointGather.Add(lastP);

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp,
                                            bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion
                                    }
                                    else if (busDuct.Type面对墙类型 == 面对墙类型.左中偏移 || busDuct.Type面对墙类型 == 面对墙类型.右中偏移)
                                    {
                                        var lastP = thisP1.Move(busDuct.XVector,
                                            bend.BusBarWidth + busDuct.B相偏移第一段长度尺寸);
                                        bend.PointGather.Add(lastP);

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                        if (i % 2 == 1)
                                        {
                                            lastP = lastP.Move(ZVector, bend.BusBarThickness);
                                        }
                                        else
                                        {
                                            lastP = lastP.Move(ZVector.Negate(), bend.BusBarThickness);
                                        }

                                        lastP = lastP.Move(busDuct.XVector.Negate(),
                                                busDuct.BendSetting.BusBarWidth / 2.0)
                                            .Move(
                                                busDuct.Type面对墙类型.ToString().Contains("右")
                                                    ? busDuct.YVector.Negate()
                                                    : busDuct.YVector,
                                                busDuct.BendSetting.BusBarWidth / 2.0);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(
                                            busDuct.Type面对墙类型.ToString().Contains("左")
                                                ? busDuct.YVector.Negate()
                                                : busDuct.YVector,
                                            busDuct.偏移尺寸 + busDuct.BendSetting.BusBarWidth);
                                        bend.PointGather.Add(lastP);

                                        bend.WidthVector = bend.StartBusbarVector
                                            .Cross(new Vector3d(bend.PointGather[0], bend.PointGather[1])).Negate();

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp,
                                            bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                        if (i % 2 == 0)
                                        {
                                            lastP = lastP.Move(ZVector, bend.BusBarThickness);
                                        }
                                        else
                                        {
                                            lastP = lastP.Move(ZVector.Negate(), bend.BusBarThickness);
                                        }

                                        lastP = lastP.Move(busDuct.XVector.Negate(),
                                                busDuct.BendSetting.BusBarWidth / 2.0)
                                            .Move(
                                                busDuct.Type面对墙类型.ToString().Contains("左")
                                                    ? busDuct.YVector
                                                    : busDuct.YVector.Negate(),
                                                busDuct.BendSetting.BusBarWidth / 2.0);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.AC相第一节直排长度 + busDuct.AC相折弯排第一段长度 - busDuct.B相偏移第一段长度尺寸
                                            - busDuct.BendSetting.BusBarWidth
                                            + GetReverseStackStaggerWithOneLayer(busDuct, j));
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(ZVector, busDuct.三相折弯高度);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.三相折弯排第二段长度 -
                                            GetReverseStackStaggerWithOneLayer(busDuct, j));

                                        bend.PointGather.Add(lastP);

                                        bend.WidthVector = bend.StartBusbarVector
                                            .Cross(new Vector3d(bend.PointGather[0], bend.PointGather[1])).Negate();

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                    }
                                    else if (busDuct.Type面对墙类型 == 面对墙类型.左铣排 || busDuct.Type面对墙类型 == 面对墙类型.右铣排)
                                    {
                                        var lastP = thisP1.Move(busDuct.XVector,
                                           busDuct.B相偏移第一段长度尺寸);
                                        bend.PointGather.Add(lastP);

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                        var bend2 = bend.Copy();
                                        bend2.BusBarWidth = bend.BusBarWidth + busDuct.偏移尺寸;
                                        lastP = lastP.Move(
                                            busDuct.Type面对墙类型.ToString().Contains("左")
                                                ? busDuct.YVector.Negate()
                                                : busDuct.YVector, busDuct.偏移尺寸 / 2.0);
                                        bend2.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector, busDuct.B相铣排两段铣排距离尺寸);
                                        bend2.PointGather.Add(lastP);
                                        #region 加入预览实体

                                        bend2.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp,
                                            bend2,
                                            color, compStandard));
                                        bend2.PointGather.Clear();

                                        #endregion

                                        lastP = lastP.Move(
                                            busDuct.Type面对墙类型.ToString().Contains("左")
                                                ? busDuct.YVector.Negate()
                                                : busDuct.YVector, busDuct.偏移尺寸 / 2.0);

                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.AC相第一节直排长度 + busDuct.AC相折弯排第一段长度 - busDuct.B相偏移第一段长度尺寸
                                            - busDuct.B相铣排两段铣排距离尺寸
                                            - busDuct.BendSetting.BusBarWidth
                                            + GetReverseStackStaggerWithOneLayer(busDuct, j));
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(ZVector, busDuct.三相折弯高度);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.三相折弯排第二段长度 -
                                            GetReverseStackStaggerWithOneLayer(busDuct, j));

                                        bend.PointGather.Add(lastP);

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion
                                    }
                                }
                                else
                                {
                                    if (busDuct.Type面对墙类型 == 面对墙类型.左立弯
                                        || busDuct.Type面对墙类型 == 面对墙类型.右立弯
                                        || busDuct.Type面对墙类型 == 面对墙类型.左中偏移
                                        || busDuct.Type面对墙类型 == 面对墙类型.右中偏移
                                        || busDuct.Type面对墙类型 == 面对墙类型.左铣排 
                                        || busDuct.Type面对墙类型 == 面对墙类型.右铣排)
                                    {
                                        var 中偏移 = 0.0;
                                        if (busDuct.Type面对墙类型 == 面对墙类型.左中偏移
                                            || busDuct.Type面对墙类型 == 面对墙类型.右中偏移)
                                        {
                                            中偏移 = busDuct.BendSetting.BusBarWidth;
                                        }

                                        var lastP = thisP1.Move(busDuct.XVector, busDuct.AC相第一节直排长度);
                                        bend.PointGather.Add(lastP);

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                        if (i % 2 == 0)
                                        {
                                            lastP = lastP.Move(ZVector, bend.BusBarThickness);
                                        }
                                        else
                                        {
                                            lastP = lastP.Move(ZVector.Negate(), bend.BusBarThickness);
                                        }

                                        lastP = lastP.Move(busDuct.XVector.Negate(),
                                                busDuct.BendSetting.BusBarWidth / 2.0)
                                            .Move(l == 0 ? busDuct.YVector.Negate() : busDuct.YVector,
                                                busDuct.BendSetting.BusBarWidth / 2.0);
                                        bend.PointGather.Add(lastP);

                                        if (l == 2)
                                        {
                                            var str = "";
                                        }

                                        lastP = lastP.Move(l == 0 ? busDuct.YVector : busDuct.YVector.Negate(),
                                            busDuct.套管相间距 - busDuct.PhaseDis +
                                            busDuct.BendSetting.BusBarWidth);

                                        if (busDuct.Type面对墙类型.ToString().Contains("左"))
                                        {
                                            lastP = lastP.Move(busDuct.YVector.Negate(), busDuct.偏移尺寸);
                                        }
                                        else
                                        {
                                            lastP = lastP.Move(busDuct.YVector, busDuct.偏移尺寸);
                                        }

                                        bend.PointGather.Add(lastP);
                                        bend.WidthVector = bend.StartBusbarVector
                                            .Cross(new Vector3d(bend.PointGather[0], bend.PointGather[1])).Negate();

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion

                                        if (i % 2 == 1)
                                        {
                                            lastP = lastP.Move(ZVector, bend.BusBarThickness);
                                        }
                                        else
                                        {
                                            lastP = lastP.Move(ZVector.Negate(), bend.BusBarThickness);
                                        }

                                        lastP = lastP.Move(l == 0 ? busDuct.YVector.Negate() : busDuct.YVector,
                                                busDuct.BendSetting.BusBarWidth / 2.0)
                                            .Move(busDuct.XVector.Negate(), busDuct.BendSetting.BusBarWidth / 2.0);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.AC相折弯排第一段长度 + GetReverseStackStaggerWithOneLayer(busDuct, j));
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(ZVector, busDuct.三相折弯高度);
                                        bend.PointGather.Add(lastP);

                                        lastP = lastP.Move(busDuct.XVector,
                                            busDuct.三相折弯排第二段长度 - GetReverseStackStaggerWithOneLayer(busDuct, j));
                                        bend.PointGather.Add(lastP);

                                        bend.WidthVector = bend.StartBusbarVector
                                            .Cross(new Vector3d(bend.PointGather[0], bend.PointGather[1])).Negate();

                                        #region 加入预览实体

                                        bend.SetPointGather();
                                        listBody.Add(UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend,
                                            color, compStandard));
                                        bend.PointGather.Clear();

                                        #endregion
                                    }
                                }


                           
                            }

                            #endregion

                            else
                            {
                                // 普通直排/桥型：奇数段 i 走硬编码桥型折弯（120/50mm）。
                                if (i % 2 == 1)
                                {
                                    bend.PointGather.Add(P1.Move(busDuct.XVector, 120));

                                    bend.PointGather.Add(bend.PointGather[1].Move(busDuct.XVector, 50.0)
                                        .Move(ZVector.Negate(), bend.BusBarThickness));

                                    bend.PointGather.Add(P2
                                        .Move(busDuct.XVector.Negate(), 120 + 50.0)
                                        .Move(ZVector.Negate(), bend.BusBarThickness));

                                    bend.PointGather.Add(P2
                                        .Move(busDuct.XVector.Negate(), 120));
                                }

                                bend.PointGather.Add(P2);
                                bend.SetPointGather();

                                // 生成母排预览实体
                                FreeDrawBody fdBody =
                                    UseBendToPreviewFreeBusBar.PreviewBendBody((SldWorks)swApp, bend, color,
                                        compStandard);
                                listBody.Add(fdBody);
                            }

                            #endregion
                        }
                    }
                }


            }
            catch (Exception e)
            {
                throw new InvalidOperationException("母排三维预览生成失败：" + e.Message, e);
            }

            return listBody;
        }

        /// <summary>
        /// 构造换相中间组的单份扫描中心线。这里不带任何叠排成员偏移，
        /// 先使用叠排总包络排厚建模，随后由拆分入口统一展开成员层位。
        /// </summary>
        private static IReadOnlyList<BendPreviewSetting>
            CreateLegacyPhaseChangeCenterlinePaths(
            BusDuctData busDuct,
            int phaseIndex,
            Point3d phaseStartPoint,
            Vector3d stackDirection,
            double stackEnvelopeThickness = 0d,
            double phaseGeometryBusbarThickness = 0d,
            double phaseChangeBendAllowance =
                LegacyPhaseChangeDefaultBendAllowance)
        {
            if (busDuct == null)
            {
                throw new ArgumentNullException(nameof(busDuct));
            }
            if (phaseStartPoint == null)
            {
                throw new ArgumentNullException(nameof(phaseStartPoint));
            }
            if (stackDirection == null || stackDirection.Norm < 1e-8)
            {
                throw new ArgumentException(
                    "换相中间组缺少有效的叠排方向。",
                    nameof(stackDirection));
            }

            var centerlineTemplate = busDuct.BendSetting.Copy();
            var effectiveStackEnvelopeThickness =
                stackEnvelopeThickness > 0d
                    ? stackEnvelopeThickness
                    : centerlineTemplate.BusBarThickness;
            var effectivePhaseGeometryBusbarThickness =
                phaseGeometryBusbarThickness > 0d
                    ? phaseGeometryBusbarThickness
                    : centerlineTemplate.BusBarThickness;
            if (effectiveStackEnvelopeThickness <= 0d ||
                double.IsNaN(effectiveStackEnvelopeThickness) ||
                double.IsInfinity(effectiveStackEnvelopeThickness) ||
                effectivePhaseGeometryBusbarThickness <= 0d ||
                double.IsNaN(effectivePhaseGeometryBusbarThickness) ||
                double.IsInfinity(effectivePhaseGeometryBusbarThickness) ||
                phaseChangeBendAllowance < 0d ||
                double.IsNaN(phaseChangeBendAllowance) ||
                double.IsInfinity(phaseChangeBendAllowance))
            {
                throw new InvalidOperationException(
                    "换相中心线缺少有效的叠排排厚。");
            }
            // 换相横向搭接的纵向折叠中心必须越过半个排宽、半个叠排包络，
            // 并额外保留默认 20 mm 折弯余量，避免红色相在自身搭接处提前收回。
            var phaseChangeLongitudinalOverlapExtension =
                centerlineTemplate.BusBarWidth / 2d +
                effectiveStackEnvelopeThickness / 2d +
                phaseChangeBendAllowance;
            centerlineTemplate.BusBarThickness =
                effectiveStackEnvelopeThickness;
            centerlineTemplate.StartBusbarVector = stackDirection.Copy();
            var centerlineStartPoint = phaseStartPoint.Move(
                busDuct.XVector.Negate(),
                centerlineTemplate.BusBarWidth / 2d);
            var centerlinePaths = new List<BendPreviewSetting>();

            if (phaseIndex == 0)
            {
                var incomingPath = centerlineTemplate.Copy();
                incomingPath.PointGather = new List<Point3d>
                {
                    centerlineStartPoint.Copy(),
                    centerlineStartPoint.Move(
                        busDuct.XVector,
                        busDuct.A_Length1),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.A_Length1)
                        .Move(stackDirection, busDuct.A_Height1),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.A_Length1)
                        .Move(stackDirection, busDuct.A_Height1)
                        .Move(busDuct.XVector, busDuct.A_Length2)
                };
                var incomingEndPoint = incomingPath.PointGather[
                    incomingPath.PointGather.Count - 1];

                var crossbarPath = centerlineTemplate.Copy();
                crossbarPath.WidthVector = busDuct.YVector
                    .Cross(stackDirection)
                    .Negate();
                var crossbarStartPoint = incomingEndPoint
                    .Move(
                        busDuct.XVector.Negate(),
                        phaseChangeLongitudinalOverlapExtension)
                    .Move(busDuct.YVector, crossbarPath.BusBarWidth / 2d)
                    .Move(stackDirection, effectivePhaseGeometryBusbarThickness);
                var crossbarEndPoint = crossbarStartPoint.Move(
                    busDuct.YVector.Negate(),
                    busDuct.PhaseDis * 2d + crossbarPath.BusBarWidth);
                crossbarPath.PointGather = new List<Point3d>
                {
                    crossbarStartPoint,
                    crossbarEndPoint
                };

                var outgoingPath = centerlineTemplate.Copy();
                var outgoingStartPoint = crossbarEndPoint
                    .Move(
                        busDuct.XVector.Negate(),
                        phaseChangeLongitudinalOverlapExtension)
                    .Move(busDuct.YVector, outgoingPath.BusBarWidth / 2d)
                    .Move(stackDirection.Negate(), effectivePhaseGeometryBusbarThickness);
                outgoingPath.PointGather = new List<Point3d>
                {
                    outgoingStartPoint,
                    outgoingStartPoint.Move(
                        busDuct.XVector,
                        busDuct.A_Length3),
                    outgoingStartPoint.Move(
                            busDuct.XVector,
                            busDuct.A_Length3)
                        .Move(stackDirection.Negate(),
                            busDuct.A_Height1 - effectivePhaseGeometryBusbarThickness),
                    outgoingStartPoint.Move(
                            busDuct.XVector,
                            busDuct.A_Length3)
                        .Move(stackDirection.Negate(),
                            busDuct.A_Height1 - effectivePhaseGeometryBusbarThickness)
                        .Move(busDuct.XVector, busDuct.A_Length4)
                };
                centerlinePaths.Add(incomingPath);
                centerlinePaths.Add(crossbarPath);
                centerlinePaths.Add(outgoingPath);
                return centerlinePaths.AsReadOnly();
            }

            if (phaseIndex == 1)
            {
                var middlePath = centerlineTemplate.Copy();
                middlePath.PointGather = new List<Point3d>
                {
                    centerlineStartPoint.Copy(),
                    centerlineStartPoint.Move(
                        busDuct.XVector,
                        busDuct.B_Length1),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.B_Length1)
                        .Move(stackDirection, busDuct.B_Height1),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.B_Length1)
                        .Move(stackDirection, busDuct.B_Height1)
                        .Move(
                            busDuct.XVector,
                            busDuct.A_Length1 +
                            busDuct.A_Length2 +
                            busDuct.A_Length3 -
                            busDuct.B_Length1 -
                            middlePath.BusBarWidth),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.B_Length1)
                        .Move(stackDirection, busDuct.B_Height1)
                        .Move(
                            busDuct.XVector,
                            busDuct.A_Length1 +
                            busDuct.A_Length2 +
                            busDuct.A_Length3 -
                            busDuct.B_Length1 -
                            middlePath.BusBarWidth)
                        .Move(
                            stackDirection.Negate(),
                            busDuct.B_Height1 -
                            effectivePhaseGeometryBusbarThickness),
                    centerlineStartPoint.Move(
                            busDuct.XVector,
                            busDuct.B_Length1)
                        .Move(stackDirection, busDuct.B_Height1)
                        .Move(
                            busDuct.XVector,
                            busDuct.A_Length1 +
                            busDuct.A_Length2 +
                            busDuct.A_Length3 -
                            busDuct.B_Length1 -
                            middlePath.BusBarWidth)
                        .Move(
                            stackDirection.Negate(),
                            busDuct.B_Height1 -
                            effectivePhaseGeometryBusbarThickness)
                        .Move(busDuct.XVector, busDuct.A_Length4)
                };
                centerlinePaths.Add(middlePath);
                return centerlinePaths.AsReadOnly();
            }

            if (phaseIndex == 2)
            {
                var incomingPath = centerlineTemplate.Copy();
                incomingPath.PointGather = new List<Point3d>
                {
                    centerlineStartPoint.Copy(),
                    centerlineStartPoint.Move(
                        busDuct.XVector,
                        busDuct.C_Length1)
                };
                var incomingEndPoint = incomingPath.PointGather[
                    incomingPath.PointGather.Count - 1];

                var crossbarPath = centerlineTemplate.Copy();
                crossbarPath.WidthVector = busDuct.YVector
                    .Cross(stackDirection)
                    .Negate();
                var crossbarStartPoint = incomingEndPoint
                    .Move(
                        busDuct.XVector.Negate(),
                        phaseChangeLongitudinalOverlapExtension)
                    .Move(busDuct.YVector.Negate(), crossbarPath.BusBarWidth / 2d)
                    .Move(stackDirection, effectivePhaseGeometryBusbarThickness);
                var crossbarEndPoint = crossbarStartPoint.Move(
                    busDuct.YVector,
                    busDuct.PhaseDis * 2d + crossbarPath.BusBarWidth);
                crossbarPath.PointGather = new List<Point3d>
                {
                    crossbarStartPoint,
                    crossbarEndPoint
                };

                var outgoingPath = centerlineTemplate.Copy();
                var outgoingStartPoint = crossbarEndPoint
                    .Move(
                        busDuct.XVector.Negate(),
                        phaseChangeLongitudinalOverlapExtension)
                    .Move(busDuct.YVector.Negate(), outgoingPath.BusBarWidth / 2d)
                    .Move(stackDirection.Negate(), effectivePhaseGeometryBusbarThickness);
                outgoingPath.PointGather = new List<Point3d>
                {
                    outgoingStartPoint,
                    outgoingStartPoint.Move(
                        busDuct.XVector,
                        busDuct.A_Length1 +
                        busDuct.A_Length2 +
                        busDuct.A_Length3 +
                        busDuct.A_Length4 -
                        busDuct.C_Length1)
                };
                centerlinePaths.Add(incomingPath);
                centerlinePaths.Add(crossbarPath);
                centerlinePaths.Add(outgoingPath);
                return centerlinePaths.AsReadOnly();
            }

            throw new ArgumentOutOfRangeException(nameof(phaseIndex));
        }

        private static BendPreviewSetting TranslateLegacyPhaseChangePath(
            BendPreviewSetting centerlinePath,
            Vector3d stackDirection,
            double stackMemberOffset)
        {
            if (centerlinePath == null)
            {
                throw new ArgumentNullException(nameof(centerlinePath));
            }
            var stackedPath = centerlinePath.Copy();
            stackedPath.PointGather = centerlinePath.PointGather
                .Select(centerlinePoint => centerlinePoint.Move(
                    stackDirection,
                    stackMemberOffset))
                .ToList();
            return stackedPath;
        }

        private static BendPreviewSetting SplitLegacyStackEnvelopePhasePath(
            BendPreviewSetting stackEnvelopePath,
            BusDuctData busDuct,
            double stackMemberOffset,
            double stackEnvelopeThickness,
            bool isVerticalBendPath)
        {
            if (stackEnvelopePath == null)
            {
                throw new ArgumentNullException(nameof(stackEnvelopePath));
            }
            if (busDuct == null)
            {
                throw new ArgumentNullException(nameof(busDuct));
            }
            if (stackEnvelopePath.PointGather == null ||
                stackEnvelopePath.PointGather.Count < 2)
            {
                throw new InvalidOperationException(
                    "特殊换相路径缺少可拆分的点列。");
            }

            var singleBusbarThickness = GetStackMemberThickness(busDuct);
            if (singleBusbarThickness <= 0d ||
                stackEnvelopeThickness <= 0d ||
                double.IsNaN(stackEnvelopeThickness) ||
                double.IsInfinity(stackEnvelopeThickness))
            {
                throw new InvalidOperationException(
                    "特殊换相路径缺少有效的叠排厚度。");
            }
            var splitMemberSetting = stackEnvelopePath.Copy();
            Vector3d splitDirection;
            double splitOffset;
            if (isVerticalBendPath)
            {
                splitDirection = stackEnvelopePath.WidthVector;
                splitOffset = stackMemberOffset +
                    singleBusbarThickness / 2.0 -
                    stackEnvelopeThickness / 2.0;
                splitMemberSetting.BusBarWidth = singleBusbarThickness;
            }
            else
            {
                splitDirection = stackEnvelopePath.StartBusbarVector;
                // 普通/抬高段的点列基准就是截面参考边；总厚度路径在
                // 构造时已经抵消了自身的 E/2，拆分时只需回到成员中心偏移。
                splitOffset = stackMemberOffset;
                splitMemberSetting.BusBarThickness = singleBusbarThickness;
            }

            if (splitDirection == null || splitDirection.Norm < 1e-8)
            {
                throw new InvalidOperationException(
                    "特殊换相路径缺少有效的拆分方向。");
            }

            splitMemberSetting.PointGather =
                stackEnvelopePath.PointGather
                    .Select(stackEnvelopePoint => stackEnvelopePoint.Move(
                        splitDirection,
                        splitOffset))
                    .ToList();
            return splitMemberSetting;
        }

        private static BendPreviewSetting CreateLegacyStackEnvelopeVerticalBend(
            BusDuctData busDuct,
            Point3d segmentStartPoint,
            Point3d segmentEndPoint,
            Vector3d stackDirection,
            double lateralOffset)
        {
            if (busDuct == null)
            {
                throw new ArgumentNullException(nameof(busDuct));
            }
            if (segmentStartPoint == null || segmentEndPoint == null)
            {
                throw new ArgumentNullException(nameof(segmentStartPoint));
            }
            if (stackDirection == null || stackDirection.Norm < 1e-8)
            {
                throw new ArgumentException(
                    "立弯叠排缺少有效的排厚方向。",
                    nameof(stackDirection));
            }

            var sourceSetting = busDuct.BendSetting;
            var sourceBusbarWidth = sourceSetting.BusBarWidth;
            var stackEnvelopeThickness = GetStackEnvelopeThickness(busDuct);
            if (sourceBusbarWidth <= 0d ||
                double.IsNaN(sourceBusbarWidth) ||
                double.IsInfinity(sourceBusbarWidth) ||
                stackEnvelopeThickness <= 0d ||
                double.IsNaN(stackEnvelopeThickness) ||
                double.IsInfinity(stackEnvelopeThickness))
            {
                throw new InvalidOperationException(
                    "立弯叠排缺少有效的铜排截面尺寸。");
            }
            var stackEnvelopeVerticalBend = sourceSetting.Copy();
            stackEnvelopeVerticalBend.BusBarThickness = sourceBusbarWidth;
            stackEnvelopeVerticalBend.BusBarWidth = stackEnvelopeThickness;
            stackEnvelopeVerticalBend.StartBusbarVector =
                busDuct.YVector.Copy();
            stackEnvelopeVerticalBend.WidthVector = busDuct.XVector
                .Cross(stackEnvelopeVerticalBend.StartBusbarVector)
                .Negate();
            stackEnvelopeVerticalBend.BarBendingRadius =
                busDuct.VBendRadius == 0
                    ? sourceBusbarWidth / 4.0
                    : busDuct.VBendRadius;

            var centerlineStartPoint = segmentStartPoint
                .Move(busDuct.XVector.Negate(), sourceBusbarWidth / 2.0);
            var centerlineEndPoint = segmentEndPoint
                .Move(busDuct.XVector, sourceBusbarWidth / 2.0);
            var verticalStartPoint = centerlineStartPoint
                .Move(stackDirection, stackEnvelopeThickness / 2.0)
                .Move(busDuct.YVector.Negate(), sourceBusbarWidth / 2.0);
            var verticalEndPoint = centerlineEndPoint
                .Move(stackDirection, stackEnvelopeThickness / 2.0)
                .Move(
                    busDuct.YVector.Negate(),
                    sourceBusbarWidth / 2.0 + lateralOffset);
            stackEnvelopeVerticalBend.PointGather = new List<Point3d>
            {
                verticalStartPoint,
                verticalStartPoint.Move(
                    busDuct.XVector,
                    sourceBusbarWidth + busDuct.BendLength),
                verticalEndPoint.Move(
                    busDuct.XVector.Negate(),
                    sourceBusbarWidth + busDuct.BendLength),
                verticalEndPoint
            };
            return stackEnvelopeVerticalBend;
        }

        private static BendPreviewSetting SplitLegacyStackEnvelopeVerticalBend(
            BendPreviewSetting stackEnvelopeVerticalBend,
            BusDuctData busDuct,
            double stackMemberOffset)
        {
            if (stackEnvelopeVerticalBend == null)
            {
                throw new ArgumentNullException(
                    nameof(stackEnvelopeVerticalBend));
            }
            if (busDuct == null)
            {
                throw new ArgumentNullException(nameof(busDuct));
            }

            var singleBusbarThickness = GetStackMemberThickness(busDuct);
            var stackEnvelopeThickness = GetStackEnvelopeThickness(busDuct);
            var splitMemberSetting = stackEnvelopeVerticalBend.Copy();
            splitMemberSetting.BusBarWidth = singleBusbarThickness;
            var splitBoundaryOffset =
                stackMemberOffset + singleBusbarThickness / 2.0 -
                stackEnvelopeThickness / 2.0;
            splitMemberSetting.PointGather =
                stackEnvelopeVerticalBend.PointGather
                    .Select(verticalBendPoint => verticalBendPoint.Move(
                        stackEnvelopeVerticalBend.WidthVector,
                        splitBoundaryOffset))
                    .ToList();
            return splitMemberSetting;
        }

        /// <summary>
        /// 返回单相叠排实际使用的成员数。叠排数量是几何展开的唯一来源，
        /// 不允许在不同铜排路径中再次写入一套独立的偏移公式。
        /// </summary>
        private static int GetStackMemberCount(BusDuctData busDuct)
        {
            return Math.Max(1, busDuct?.BusbarNum ?? 0);
        }

        /// <summary>
        /// 获取单根铜排厚度；无效输入时返回零，由调用方的输入校验负责报错。
        /// </summary>
        private static double GetStackMemberThickness(BusDuctData busDuct)
        {
            var thickness = busDuct?.BendSetting?.BusBarThickness ?? 0d;
            return double.IsNaN(thickness) || double.IsInfinity(thickness) ||
                   thickness < 0d
                ? 0d
                : thickness;
        }

        /// <summary>
        /// RouteLinkBar 叠排规则：以扫描中心线为零点，成员中心按 2t 对称展开。
        /// 例如 4 根、厚度 8 mm 时为 -24/-8/8/24 mm。
        /// </summary>
        private static double GetCenteredStackMemberOffset(
            BusDuctData busDuct,
            int memberIndex)
        {
            var memberCount = GetStackMemberCount(busDuct);
            var thickness = GetStackMemberThickness(busDuct);
            return (2d * memberIndex - (memberCount - 1d)) * thickness;
        }

        private static IReadOnlyList<double> GetCenteredStackMemberOffsets(
            BusDuctData busDuct)
        {
            var memberCount = GetStackMemberCount(busDuct);
            var centeredOffsets = new List<double>(memberCount);
            for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                centeredOffsets.Add(
                    GetCenteredStackMemberOffset(busDuct, memberIndex));
            }

            return centeredOffsets.AsReadOnly();
        }

        /// <summary>
        /// 叠排成员之间的中心距。保留端部 L 排原有的逐层复制方式，
        /// 但统一由同一套中心线叠排规则计算。
        /// </summary>
        private static double GetStackMemberPitch(BusDuctData busDuct)
        {
            return 2d * GetStackMemberThickness(busDuct);
        }

        /// <summary>
        /// 换相路径中从外层向内层逐根收回的纵向错开量，等价于
        /// max(centerOffset) - centerOffset(memberIndex)。
        /// </summary>
        private static double GetReverseStackStagger(
            BusDuctData busDuct,
            int memberIndex)
        {
            var outermostMemberOffset = GetCenteredStackMemberOffset(
                busDuct,
                GetStackMemberCount(busDuct) - 1);
            return outermostMemberOffset -
                   GetCenteredStackMemberOffset(busDuct, memberIndex);
        }

        /// <summary>
        /// 换相路径需要额外跨过一根排厚时的纵向错开量。
        /// </summary>
        private static double GetReverseStackStaggerWithOneLayer(
            BusDuctData busDuct,
            int memberIndex)
        {
            return GetReverseStackStagger(busDuct, memberIndex) +
                   GetStackMemberPitch(busDuct);
        }

        /// <summary>
        /// 换相路径从内层向外层递增的纵向错开量。
        /// </summary>
        private static double GetForwardStackStagger(
            BusDuctData busDuct,
            int memberIndex)
        {
            return GetCenteredStackMemberOffset(busDuct, memberIndex) -
                   GetCenteredStackMemberOffset(busDuct, 0);
        }

        /// <summary>
        /// 叠排包络厚度 t + (n - 1) * 2t = (2n - 1)t。
        /// </summary>
        private static double GetStackEnvelopeThickness(BusDuctData busDuct)
        {
            return (2d * GetStackMemberCount(busDuct) - 1d) *
                   GetStackMemberThickness(busDuct);
        }

        /// <summary>
        /// 桥身铜排段数 = 内部断点数 + 1（与 positionList.Count - 1 一致）。
        /// </summary>
        private static int GetSegmentCount(BusDuctData busDuct)
        {
            if (busDuct == null)
            {
                return 0;
            }

            return (busDuct.OtherPositions?.Count ?? 0) + 1;
        }
    }
}
