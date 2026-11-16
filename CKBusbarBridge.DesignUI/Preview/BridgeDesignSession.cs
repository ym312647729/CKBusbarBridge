using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // Owned by one assembly document. This state must never contain COM references.
    internal sealed class BridgeDesignSession
    {
        internal const string RuntimeVersion = "dq-sw-mesh/1";
        internal Guid Id { get; private set; }
        internal string Scenario { get; private set; }
        internal long Revision { get; private set; }
        internal long ReadyRevision { get; private set; } = -1;
        internal bool Generating { get; private set; }
        internal string Failure { get; private set; }
        internal string DrawingMetadata { get; set; }
        internal string CabinetOrientation { get; set; }
        internal string RuntimePackageVersion { get; set; }
        internal BridgeUpstreamSnapshot Upstream { get; private set; }
        internal BridgeMeshFrame Frame { get; private set; }
        private JObject _parameters;
        private JObject _runtimeProject;
        private string _recipe;
        private long _editorRevision = -1;
        private bool _recoveryBlocked;
        internal event EventHandler Changed;
        internal event EventHandler Ready;

        internal BridgeDesignSession(string scenario, JObject parameters, Guid? id = null, long revision = 0)
        {
            if (!new[] { "face-to-face", "side-to-side", "face-to-wall" }.Contains(scenario))
                throw new ArgumentException("不支持的母线桥草稿场景：" + scenario);
            Id = id ?? Guid.NewGuid(); Scenario = scenario; Revision = revision;
            _parameters = SinglePrecision.Copy(parameters);
        }

        internal JObject Parameters() => (JObject)_parameters.DeepClone();
        internal JObject RuntimeProject() => _runtimeProject == null ? null : (JObject)_runtimeProject.DeepClone();
        internal string Recipe => _recipe;
        internal bool RecoveryBlocked => _recoveryBlocked;
        internal bool IsReady => !Generating && Failure == null && ReadyRevision == Revision && Frame != null && Upstream != null;

        internal long UpdateParameters(JObject parameters)
        {
            if (_recoveryBlocked) throw new InvalidOperationException(Failure);
            if (Generating) throw new InvalidOperationException("正式生成期间不能修改柜体参数。");
            var next = SinglePrecision.Copy(parameters);
            if (JToken.DeepEquals(next, _parameters)) return Revision;
            _parameters = next; return Invalidate();
        }

        internal void ResetCabinetScenario(string scenario, JObject parameters, string orientation, string drawingMetadata)
        {
            if (_recoveryBlocked || Generating) throw new InvalidOperationException(Failure ?? "正式生成期间不能切换场景。");
            if (scenario != "face-to-face" && scenario != "side-to-side" && scenario != "face-to-wall")
                throw new ArgumentException("不支持的母线桥草稿场景：" + scenario, nameof(scenario));
            var next = SinglePrecision.Copy(parameters);
            Scenario = scenario; CabinetOrientation = orientation; DrawingMetadata = drawingMetadata;
            _parameters = next; _editorRevision = -1; _recipe = null; _runtimeProject = null; Upstream = null; Frame = null;
            Invalidate();
        }

        internal long UpdateDraft(long editorRevision, string recipe)
        {
            if (Generating) throw new InvalidOperationException("正式生成期间不能修改方案。");
            if (_editorRevision == editorRevision && _recipe == recipe) return Revision;
            _editorRevision = editorRevision; _recipe = recipe; return Invalidate();
        }

        internal long Invalidate()
        {
            if (_recoveryBlocked) throw new InvalidOperationException(Failure);
            Revision = checked(Revision + 1); ReadyRevision = -1; Failure = null;
            // Retain the last frame for display only; IsReady blocks editing and generation.
            Changed?.Invoke(this, EventArgs.Empty); return Revision;
        }

        internal bool Install(Guid sessionId, long revision, JObject project, BridgeUpstreamSnapshot upstream, BridgeMeshFrame frame)
        {
            if (sessionId != Id || revision != Revision || Generating) return false;
            _runtimeProject = SinglePrecision.Copy(project); Upstream = upstream;
            Frame = frame; ReadyRevision = revision; Failure = null; Ready?.Invoke(this, EventArgs.Empty); return true;
        }

        internal void Reject(Guid sessionId, long revision, Exception failure)
        {
            if (sessionId != Id || revision != Revision) return;
            Failure = failure.Message; ReadyRevision = -1;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal void BlockRecovery(string message) { _recoveryBlocked = true; Failure = message; ReadyRevision = -1; Frame = null; }

        internal IDisposable Freeze(long expectedRevision)
        {
            if (!IsReady || Revision != expectedRevision) throw new InvalidOperationException("草稿尚未就绪或预览已过期，请等待当前参数计算完成。");
            Generating = true; return new GenerationLease(this);
        }

        internal JObject SaveState(string assemblyPath, string recipe = null)
        {
            return new JObject { ["schema"] = RuntimeVersion, ["sessionId"] = Id.ToString("D"),
                ["scenario"] = Scenario, ["revision"] = Revision, ["assemblyPath"] = assemblyPath,
                ["parameters"] = Parameters(), ["project"] = RuntimeProject(), ["recipe"] = recipe ?? _recipe,
                ["recoveryBlocked"] = _recoveryBlocked, ["failure"] = Failure, ["runtimePackageVersion"] = RuntimePackageVersion };
        }

        internal static BridgeDesignSession Restore(JObject stored)
        {
            if ((string)stored["schema"] != RuntimeVersion) throw new InvalidOperationException("母线桥草稿或模板计算器版本不匹配。");
            var result = new BridgeDesignSession((string)stored["scenario"], (JObject)stored["parameters"],
                Guid.Parse((string)stored["sessionId"]), (long)stored["revision"]);
            result._runtimeProject = stored["project"] as JObject;
            result._recipe = (string)stored["recipe"];
            result.RuntimePackageVersion = (string)stored["runtimePackageVersion"];
            if ((bool?)stored["recoveryBlocked"] == true) result.BlockRecovery((string)stored["failure"]);
            return result;
        }

        private sealed class GenerationLease : IDisposable
        {
            private BridgeDesignSession _owner;
            internal GenerationLease(BridgeDesignSession owner) { _owner = owner; }
            public void Dispose() { if (_owner == null) return; _owner.Generating = false; _owner = null; }
        }
    }

    internal static class SinglePrecision
    {
        internal const float Tolerance = 1.0f;
        internal static float Number(JToken token)
        {
            var value = token.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("草稿含非有限工程数值。");
            return value;
        }
        internal static JObject Copy(JObject source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var copy = (JObject)source.DeepClone();
            foreach (var number in copy.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.Float).ToArray())
                number.Value = Number(number);
            return copy;
        }
    }

    internal sealed class BridgeMeshFrame
    {
        internal Guid SessionId { get; }
        internal long Revision { get; }
        internal IReadOnlyList<BridgeMeshInstance> Instances { get; }
        internal IReadOnlyList<BridgeMeshInstance> InteractionInstances { get; }
        internal string Warning { get; }
        internal float[] Minimum { get; }
        internal float[] Maximum { get; }
        internal BridgeMeshFrame(Guid sessionId, long revision, IEnumerable<BridgeMeshInstance> instances, string warning, IEnumerable<BridgeMeshInstance> interactionInstances = null)
        {
            SessionId = sessionId; Revision = revision; Instances = Array.AsReadOnly(instances.ToArray()); Warning = warning;
            InteractionInstances = interactionInstances == null ? Instances : Array.AsReadOnly(interactionInstances.ToArray());
            foreach (var group in new[] { Instances, InteractionInstances })
                if (group.Select(instance => instance.Id).Distinct(StringComparer.Ordinal).Count() != group.Count)
                    throw new InvalidOperationException("预览实例身份重复。");
            if (!Instances.Select(instance => instance.Id).OrderBy(id => id).SequenceEqual(InteractionInstances.Select(instance => instance.Id).OrderBy(id => id)))
                throw new InvalidOperationException("交互网格与显示网格实例身份不一致。");
            Minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
            Maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
            foreach (var instance in Instances) for (var offset = 0; offset < instance.Positions.Length; offset += 3)
                for (var axis = 0; axis < 3; axis++)
                {
                    var matrix = instance.Transform;
                    var position = matrix[axis] * instance.Positions[offset] + matrix[4 + axis] * instance.Positions[offset + 1] +
                        matrix[8 + axis] * instance.Positions[offset + 2] + matrix[12 + axis];
                    Minimum[axis] = Math.Min(Minimum[axis], position); Maximum[axis] = Math.Max(Maximum[axis], position);
                }
            if (Minimum.Any(value => float.IsInfinity(value) || float.IsNaN(value)) || Maximum.Any(value => float.IsInfinity(value) || float.IsNaN(value)))
                throw new InvalidOperationException("预览没有有效的显示范围。");
        }
    }

    internal sealed class BridgeMeshInstance
    {
        internal string Id { get; }
        internal string DesignObjectId { get; }
        internal string GroupId { get; }
        internal string DisplayName { get; }
        internal string SourcePath { get; }
        internal IReadOnlyList<string> MemberIds { get; }
        internal string GeometryKey { get; }
        internal string Category { get; }
        internal string AppearanceRole { get; }
        internal string Phase { get; }
        internal float[] Transform { get; }
        internal BridgeMeshShape Shape { get; }
        internal float[] Positions => Shape.Positions;
        internal int[] Quads => Shape.Quads;
        internal int[] Triangles => Shape.Triangles;
        internal int[] Edges => Shape.Edges;
        internal bool OutlineOnly { get; }
        internal BridgeMeshInstance(JObject value, BridgeMeshShape shape)
        {
            Id = (string)value["id"]; GeometryKey = (string)value["geometryKey"];
            DesignObjectId = (string)value["designObjectId"]; GroupId = (string)value["groupId"];
            DisplayName = (string)value["displayName"] ?? DesignObjectId; SourcePath = (string)value["sourcePath"];
            MemberIds = Array.AsReadOnly((value["memberIds"]?.Values<string>() ?? Array.Empty<string>()).ToArray());
            Category = (string)value["category"]; Phase = (string)value["phase"];
            AppearanceRole = (string)value["appearanceRole"];
            Transform = value["transform"].Select(SinglePrecision.Number).ToArray();
            Shape = shape ?? throw new ArgumentNullException(nameof(shape)); OutlineOnly = (bool?)value["outlineOnly"] == true;
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(DesignObjectId) || MemberIds.Any(string.IsNullOrWhiteSpace) || MemberIds.Distinct().Count() != MemberIds.Count || Transform.Length != 16)
                throw new InvalidOperationException("预览实例的设计对象身份或变换无效。");
        }
    }
    // Arrays are owned here and consumed read-only by the renderer; installation/style never mutate shape.
    internal sealed class BridgeMeshShape
    {
        internal string Signature { get; }
        internal float[] Positions { get; }
        internal float[] EdgePositions { get; }
        internal int[] Quads { get; }
        internal int[] Triangles { get; }
        internal int[] Edges { get; }
        internal float[] QuadNormals { get; }
        internal float[] TriangleNormals { get; }
        internal CKBusbarBridge.DesignPreview.BridgeTriangleIndex PickingIndex { get; }
        internal long Bytes => (long)(Positions.Length + EdgePositions.Length + Quads.Length + Triangles.Length + Edges.Length + QuadNormals.Length + TriangleNormals.Length) * 4;
        internal BridgeMeshShape(JObject value)
        {
            Signature = (string)value["geometrySignature"] ?? throw new InvalidOperationException("网格缺少完整输入签名。");
            Positions = value["positions"].Select(SinglePrecision.Number).ToArray();
            EdgePositions = (value["edgePositions"] ?? value["positions"]).Select(SinglePrecision.Number).ToArray();
            Quads = value["quads"].Values<int>().ToArray(); Triangles = value["triangles"].Values<int>().ToArray(); Edges = value["edges"].Values<int>().ToArray();
            if (Positions.Length % 3 != 0 || EdgePositions.Length % 3 != 0 ||
                Quads.Length % 4 != 0 || Triangles.Length % 3 != 0 || Edges.Length % 2 != 0 ||
                Quads.Concat(Triangles).Any(index => index < 0 || index >= Positions.Length / 3) || Edges.Any(index => index < 0 || index >= EdgePositions.Length / 3))
                throw new InvalidOperationException("预览网格拓扑无效。");
            QuadNormals = Normals(Quads, 4); TriangleNormals = Normals(Triangles, 3);
            PickingIndex = new CKBusbarBridge.DesignPreview.BridgeTriangleIndex(this);
        }
        private float[] Normals(int[] indices, int stride)
        {
            var normals = new float[indices.Length / stride * 3];
            for (var offset = 0; offset < indices.Length; offset += stride)
            {
                var nx = 0f; var ny = 0f; var nz = 0f;
                // Polygon area normal also handles quads whose first three corners are collinear after a boundary split.
                var anchor = indices[offset] * 3;
                for (var corner = 1; corner < stride - 1; corner++)
                {
                    var b = indices[offset + corner] * 3; var c = indices[offset + corner + 1] * 3;
                    var ux = Positions[b] - Positions[anchor]; var uy = Positions[b + 1] - Positions[anchor + 1]; var uz = Positions[b + 2] - Positions[anchor + 2];
                    var vx = Positions[c] - Positions[anchor]; var vy = Positions[c + 1] - Positions[anchor + 1]; var vz = Positions[c + 2] - Positions[anchor + 2];
                    nx += uy * vz - uz * vy; ny += uz * vx - ux * vz; nz += ux * vy - uy * vx;
                }
                var length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (float.IsNaN(length) || float.IsInfinity(length)) throw new InvalidOperationException("预览网格法向含非法坐标。");
                if (length == 0f) continue; // A collinear compatibility primitive has no area to shade.
                var normalOffset = offset / stride * 3; normals[normalOffset] = nx / length; normals[normalOffset + 1] = ny / length; normals[normalOffset + 2] = nz / length;
            }
            return normals;
        }
    }
}
