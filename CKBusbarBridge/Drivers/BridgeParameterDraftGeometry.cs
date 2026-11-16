using System;
using System.Linq;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeCopperHolePattern
    {
        internal string Location, Style;
        internal float Diameter, SlotLength;
    }
    internal static class BridgeParameterDraftGeometry
    {
        // The original planner/editor is authoritative. JS consumes only frozen geometry and drive snapshots.
        internal static JObject Export(BusbarBridgeDraftEditor editor, BridgeUpstreamSnapshot upstream, BridgeCopperHolePattern pattern)
        {
            var sample = editor.CurrentSample; var objects = editor.EffectivePlan;
            if (sample == null || upstream == null) return null;
            var frameNodes = objects.Where(node => node.Kind == DesignObjectKind.Frame).OrderBy(node => node.FramePositionIndex).ToArray();
            var factory = new BridgeDraftGenerationPlanFactory();
            var wall = upstream.FaceToWall != null && !editor.HasBlockingIssues ? factory.WallPlans(upstream.FaceToWall, frameNodes, objects) : null;
            var coordinates = sample.Coordinates ?? BridgeCoordinateFrame.Facing(sample.PreviewOriginX.Value, sample.PreviewOriginZ.Value);
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            if (pattern == null) throw new InvalidOperationException("铜排预览缺少开孔工艺输入。");
            Func<double[], float[]> previewPoint = point => { var native = coordinates.ToAssembly(point); return new[] { (float)native[0], -(float)native[2], (float)native[1] }; };
            var previewOrigin = previewPoint(new[] { 0d, 0d, 0d });
            object CornerInstallation(BridgeDesignObject frame)
            {
                var route = BridgeDraftGenerationPlanFactory.FrameInstallationCoordinates(frame);
                var origin = coordinates.ToAssembly(new[] { frame.X, frame.Y, frame.Z });
                // 桥向长度对应 B 侧入口／A 侧出口；028 模板长度轴对应进1／出2。
                var bridgeLengthAtInlet = frame.CornerPreview.ClosedEnd > 0;
                var templateLengthAtInlet = route.Inlet == "坐标系-进1";
                var swapTemplateAxes = bridgeLengthAtInlet != templateLengthAtInlet;
                var bridgeLength = frame.DriveValue(DesignField.Length);
                var bridgeWidth = frame.DriveValue(DesignField.Width);
                return new { inletCoordinateName = route.Inlet, outletCoordinateName = route.Outlet,
                    center = new[] { origin[0], -origin[2], origin[1] },
                    templateLength = swapTemplateAxes ? bridgeWidth : bridgeLength,
                    templateWidth = swapTemplateAxes ? bridgeLength : bridgeWidth };
            }
            var settings = BridgeWallPlanningCodec.CreateSettings(); settings.ContractResolver = new DefaultContractResolver(); settings.Converters.Add(new StringEnumConverter());
            var snapshot = new
            {
                version = "original-joint-design/1", status = editor.HasBlockingIssues ? "draft" : "ready", scenario = sample.ScenarioKey,
                holePattern = new { location = pattern.Location, style = pattern.Style, diameter = pattern.Diameter, slotLength = pattern.SlotLength },
                rootCount = upstream.FaceToWall?.RootCount ?? upstream.SideToSide?.Copper.RootCount ?? upstream.Endpoints.Count(endpoint => endpoint.Side == "A" && endpoint.Phase == "A"),
                insulatorConnections = objects.Where(node => node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator)
                    .Where(node => !node.IsSuppressedInsulator).Select(node => new { node.Id, Pose = geometry.HoleConnection(node, objects) })
                    .Where(item => item.Pose != null).Select(item => new { id = item.Id,
                        center = previewPoint(item.Pose.Connection.ToArray()),
                        normal = previewPoint(item.Pose.Axis.ToArray()).Select((value, axis) => value - previewOrigin[axis]).ToArray() }),
                frameDriveContext = wall == null ? null : new { regularFrameWidth = upstream.FaceToWall.Cabinet.BaseWidth,
                    wallCabinetEccentricity = upstream.FaceToWall.EffectiveWallEccentricity, baseOpeningHeight = upstream.FaceToWall.Cabinet.OpeningHeight },
                frameInsulatorDrives = frameNodes.Select(frame => new { frameId = frame.Id,
                    transverseOffsets = frame.UseOffsetInsulators ? factory.TransverseOffsets(frame, objects) : null,
                    mounts = factory.MountPlans(frame, objects) }),
                frameDrives = wall?.Select(position => new { positionIndex = position.PositionIndex, sourceName = position.SourceName,
                    width = position.ModelPlan.Width, length = position.ModelPlan.Length, height = position.ModelPlan.Height,
                    insulatorCount = position.ModelPlan.InsulatorCount, insulatorPositions = position.ModelPlan.InsulatorPositions,
                    wallEntryDistance = position.ModelPlan.WallEntryDistance, globalVariables = position.ModelPlan.GlobalVariables,
                    inletCoordinateName = position.InletCoordinateName, outletCoordinateName = position.OutletCoordinateName }),
                objects = objects.Select(node => new { id = node.Id, name = node.Name, kind = node.Kind.ToString(), parentId = node.ParentId,
                    sourceName = node.SourceName, framePositionIndex = node.FramePositionIndex, frameTemplateKey = node.FrameTemplateKey,
                    values = node.Values, isReversed = node.IsReversed, smallOffsetPreview = node.SmallOffsetPreview,
                    suppressTemplateAngleNotches = node.SuppressTemplateAngleNotches,
                    insulatorAssemblyHeight = node.InsulatorAssemblyHeight, insulatorPhaseSpacing = node.InsulatorPhaseSpacing,
                    useOffsetInsulators = node.UseOffsetInsulators, insulatorPadCount = node.InsulatorPadCount, insulatorPadThickness = node.InsulatorPadThickness,
                    mountPlan = node.MountPlan, insulatorGroupId = node.InsulatorGroupId,
                    previewPorts = node.Kind == DesignObjectKind.Frame ? geometry.FrameInterfacePoints(node).Select(coordinates.ToAssembly)
                        .Select(point => new[] { (float)point[0], -(float)point[2], (float)point[1] }).ToArray() : null }),
                rows = editor.InsulatorObjects.Select(row => new { id = row.Id, kind = "insulator", memberIds = row.MemberIds,
                    fields = new[] { new { field = "InsulatorPosition", value = row.MemberIds.Select(editor.Find)
                        .Average(node => node.X + editor.Find(node.ParentId).Length / 2), readOnly = row.Error != null } } }),
                routes = sample.Routes.Select(route => new { id = route.Id, phase = route.Phase, generationGroup = route.GenerationGroup,
                    copperSectionId = route.CopperSectionId, nativeGeometry = route.CopyNativePreview() })
            };
            var result = SinglePrecision.Copy(JObject.FromObject(snapshot, JsonSerializer.Create(settings)));
            result["hangers"] = JArray.FromObject(editor.HangerPlacements.Where(row => row.IsValid).Select(row => new
            {
                id = row.Id, frameId = row.Frame.Id, offset = row.Pose.LocalOffset.Value,
                center = row.Pose.Origin, installationHeight = row.Plan.InstallationHeight
            }));
            // 安装中心及模板局部驱动尺寸保留双精度；原网格快照契约继续独立处理。
            var cornerInstallations = frameNodes.Where(frame => frame.FrameTemplateKey == "LargeOffsetCorner")
                .ToDictionary(frame => frame.Id, CornerInstallation, StringComparer.Ordinal);
            foreach (var row in result["objects"].Children<JObject>())
                if (cornerInstallations.TryGetValue((string)row["id"], out var installation))
                    row["cornerInstallation"] = JObject.FromObject(installation);
            return result;
        }
    }
}
