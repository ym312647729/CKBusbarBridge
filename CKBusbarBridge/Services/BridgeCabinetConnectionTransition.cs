using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Services
{
    /// <summary>只接纳基座事务明确提交且安装链完整的转换，其余模型与总装配合仍走严格比较。</summary>
    internal sealed class BridgeCabinetConnectionTransition
    {
        internal void RequireCompatibleBaseline(BridgeModelState recorded, BridgeModelState expanded, ISet<string> frames)
        {
            var comparer = new BridgeModelStateComparer();
            comparer.RequireComplete(recorded, "原安装基线：");
            comparer.RequireComplete(expanded, "完整安装基线：");
            if (expanded.Installations == null)
            {
                comparer.Compare(recorded, expanded).RequireVerified();
                return;
            }
            RequireCurrentInstallations(expanded, frames);
            if (recorded.Installations != null)
            {
                if (recorded.Installations.Count != expanded.Installations.Count || recorded.Installations.Any(p =>
                    !expanded.Installations.TryGetValue(p.Key, out var installation) || !BaseInstallationChangeFactory.Same(p.Value, installation)))
                    throw Failure("修改前安装映射与正式基线不一致。");
                comparer.Compare(recorded, expanded).RequireVerified();
                return;
            }
            var additions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var installation in expanded.Installations.Values)
            {
                // 只补齐旧普通基座叠装配合已明确引用的增高根，不从修改后现场或任意新增组件补造历史。
                BindComponent(recorded, installation.Ordinary, false);
                var elevated = installation.Elevated;
                if (elevated == null || recorded.Instances.ContainsKey(elevated.Instance)) continue;
                var upper = new BaseInstallationComponent { Instance = elevated.Instance, Configuration = elevated.Configuration, Coordinate = "上" };
                var proof = recorded.Connections.Values.Count(d => d.Connection?.Endpoints?.Count == 2 &&
                    (Matches(d, d.Connection.Endpoints[0], upper) && Matches(d, d.Connection.Endpoints[1], installation.Ordinary) ||
                     Matches(d, d.Connection.Endpoints[1], upper) && Matches(d, d.Connection.Endpoints[0], installation.Ordinary)));
                if (proof != 1 || !additions.Add(elevated.Instance))
                    throw Failure("旧快照未唯一记录" + elevated.Instance + "的上坐标系叠装连接，不能补齐修改前安装依赖。");
            }
            if (expanded.Instances.Any(p => additions.Contains(p.Value.Root) && !p.Value.IsRoot && !recorded.Instances.ContainsKey(p.Key)))
                throw Failure("补齐安装依赖不能扩大旧快照的基座子件范围。");
            var instances = expanded.Instances.Where(p => !additions.Contains(p.Value.Root))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var documents = new HashSet<string>(instances.Values.Select(i => i.Document), StringComparer.OrdinalIgnoreCase);
            var projected = new BridgeModelState { Version = expanded.Version, AssemblyFileHash = expanded.AssemblyFileHash,
                FileHashes = expanded.FileHashes, Instances = instances,
                Documents = expanded.Documents.Where(p => documents.Contains(p.Key) || recorded.Documents.ContainsKey(p.Key))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                Connections = expanded.Connections.Where(p => p.Value.Connection.Endpoints.Any(e =>
                    !string.IsNullOrWhiteSpace(e.Instance) && !additions.Contains(e.Instance.Split('/')[0])))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
            comparer.Compare(recorded, projected).RequireVerified();
        }

        internal void RequireCurrentInstallations(BridgeModelState state, ISet<string> frames)
        {
            if (state?.Installations == null || state.Installations.Count == 0 || state.Installations.Any(p =>
                string.IsNullOrWhiteSpace(p.Key) || p.Value?.Ordinary == null))
                throw Failure("完整快照缺少已核验的基座安装映射。");
            var changes = state.Installations.Select(p => new BaseInstallationChange { Position = p.Key, Before = p.Value, After = p.Value }).ToArray();
            var allowed = new HashSet<string>(state.Installations.Values.SelectMany(i => new[] { i.Ordinary, i.Elevated })
                .Where(c => c != null).Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            Prepare(state, state, changes, allowed, frames);
        }

        internal BaseInstallationChange[] Prepare(BridgeModelState before, BridgeModelState after,
            IEnumerable<BaseInstallationChange> committedChanges, ISet<string> allowedDocuments, ISet<string> frameInstances)
        {
            var comparer = new BridgeModelStateComparer();
            comparer.RequireComplete(before, "安装转换前：");
            comparer.RequireComplete(after, "安装转换后：");
            var changes = Copy((committedChanges ?? Enumerable.Empty<BaseInstallationChange>()).ToArray());
            if (changes.Any(c => c == null)) throw Failure("基座事务包含空转换记录。");
            foreach (var change in changes)
            {
                if (change == null || change.FrameAnchor != null || change.BridgeCoordinate != null)
                    throw Failure("基座事务包含无效或预填的首基架锚点。");
                Bind(before, change.Before, true);
                Bind(after, change.After, true);
                if (change.Before?.Ordinary == null) continue;
                var ordinary = change.Before.Ordinary.Instance;
                var anchors = before.Connections.Values.Select(c => c.Connection).Where(c => c?.Endpoints?.Count == 2 &&
                    c.Endpoints.Any(e => e?.Instance == ordinary) && c.Endpoints.Any(e => e != null && frameInstances.Contains(e.Instance))).ToArray();
                if (anchors.Length > 1) throw Failure(change.Position + "存在多个首基架锚点。");
                if (anchors.Length == 0) continue;
                var endpoint = anchors[0].Endpoints.Single(e => e.Instance != ordinary);
                var baseEndpoint = anchors[0].Endpoints.Single(e => e.Instance == ordinary);
                var document = Document(before, endpoint.Instance);
                change.FrameAnchor = new BaseInstallationComponent { Instance = endpoint.Instance, Path = document.Path,
                    Configuration = endpoint.Configuration, Coordinate = endpoint.Reference };
                change.BridgeCoordinate = baseEndpoint.Reference;
            }
            Compare(before, after, changes, allowedDocuments, frameInstances);
            return changes;
        }

        internal void Compare(BridgeModelState before, BridgeModelState after, IEnumerable<BaseInstallationChange> authorizedChanges,
            ISet<string> allowedDocuments, ISet<string> frameInstances)
        {
            var comparer = new BridgeModelStateComparer();
            comparer.RequireComplete(before, "安装转换前：");
            comparer.RequireComplete(after, "安装转换后：");
            var changes = (authorizedChanges ?? Enumerable.Empty<BaseInstallationChange>()).ToArray();
            if (changes.Any(c => c == null || string.IsNullOrWhiteSpace(c.Position)) ||
                changes.Select(c => c.Position).Distinct(StringComparer.Ordinal).Count() != changes.Length)
                throw Failure("基座事务位置缺失或重复。");
            if (changes.Length == 0)
            {
                comparer.Compare(before, after, allowedDocuments, allowRootPlacement: true).RequireVerified();
                return;
            }
            if (before.Connections.Values.Concat(after.Connections.Values).Any(c => c.Connection?.Endpoints == null ||
                c.Connection.Endpoints.Any(e => e == null)))
                throw Failure("旧快照缺少总装配合端点，不能接管基座结构转换；请先完成原待同步桥体的核验。");

            var oldConsumed = new HashSet<string>(StringComparer.Ordinal);
            var newConsumed = new HashSet<string>(StringComparer.Ordinal);
            var oldInstances = new HashSet<string>(StringComparer.Ordinal);
            var newInstances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (change.Before == null && change.After == null || change.Before != null && change.After != null &&
                    change.Before.RootCoordinate != change.After.RootCoordinate)
                    throw Failure(change.Position + "改变了安装位置所属的总装坐标系。");
                Bind(before, change.Before, false);
                Bind(after, change.After, false);
                CheckReferences(change.Before, allowedDocuments, oldInstances);
                CheckReferences(change.After, allowedDocuments, newInstances);
                if (change.FrameAnchor != null)
                {
                    if (!frameInstances.Contains(change.FrameAnchor.Instance) || string.IsNullOrWhiteSpace(change.BridgeCoordinate) ||
                        change.Before == null || change.After == null)
                        throw Failure(change.Position + "首基架锚点不属于原正式桥体或已失去基座。");
                    BindComponent(before, change.FrameAnchor, false);
                    BindComponent(after, change.FrameAnchor, false);
                }
                else if (change.BridgeCoordinate != null) throw Failure(change.Position + "首基架锚点记录不完整。");
                CheckChain(before, change.Before, change, oldConsumed);
                CheckChain(after, change.After, change, newConsumed);
            }
            RequireNoOtherConnections(before, oldInstances, oldConsumed);
            RequireNoOtherConnections(after, newInstances, newConsumed);
            comparer.Compare(Without(before, oldConsumed), Without(after, newConsumed), allowedDocuments,
                allowRootPlacement: true).RequireVerified();
        }

        internal BaseInstallationChange[] Compose(IEnumerable<BaseInstallationChange> previous, IEnumerable<BaseInstallationChange> current)
        {
            var changes = Copy((previous ?? Enumerable.Empty<BaseInstallationChange>()).ToArray()).ToList();
            foreach (var step in Copy((current ?? Enumerable.Empty<BaseInstallationChange>()).ToArray()))
            {
                var original = changes.SingleOrDefault(c => c.Position == step.Position);
                if (original == null) { changes.Add(step); continue; }
                if (!BaseInstallationChangeFactory.Same(original.After, step.Before) ||
                    !BaseInstallationChangeFactory.Same(original.FrameAnchor, step.FrameAnchor) || original.BridgeCoordinate != step.BridgeCoordinate)
                    throw Failure(step.Position + "连续基座转换与前次提交结果不一致。");
                original.After = step.After;
            }
            return changes.ToArray();
        }

        private static void Bind(BridgeModelState state, BaseInstallationState installation, bool captureConfiguration)
        {
            if (installation == null) return;
            if (string.IsNullOrWhiteSpace(installation.RootCoordinate) || installation.Ordinary == null)
                throw Failure("基座安装契约缺少普通基座或总装坐标系。");
            BindComponent(state, installation.Ordinary, captureConfiguration);
            if (installation.Elevated != null)
            {
                if (installation.Elevated.Coordinate != "下") throw Failure("增高基座必须通过下坐标系安装。");
                BindComponent(state, installation.Elevated, captureConfiguration);
            }
        }

        private static void BindComponent(BridgeModelState state, BaseInstallationComponent component, bool captureConfiguration)
        {
            if (component == null || string.IsNullOrWhiteSpace(component.Coordinate) || string.IsNullOrWhiteSpace(component.Path))
                throw Failure("安装组件的路径或坐标系缺失。");
            var document = Document(state, component.Instance);
            if (!string.Equals(document.Path, component.Path, StringComparison.OrdinalIgnoreCase) ||
                !captureConfiguration && string.IsNullOrWhiteSpace(component.Configuration) ||
                component.Configuration != null && component.Configuration != document.Configuration)
                throw Failure(component.Instance + "安装组件的文件或引用配置与事务记录不一致。");
            if (captureConfiguration) component.Configuration = document.Configuration;
        }

        private static BridgeModelDocumentState Document(BridgeModelState state, string instance)
        {
            if (string.IsNullOrWhiteSpace(instance) || instance.Contains('/') || !state.Instances.TryGetValue(instance, out var model) ||
                !model.IsRoot || model.Root != instance || model.Suppressed || !state.Documents.TryGetValue(model.Document, out var document))
                throw Failure(instance + "不是已解析、启用且唯一的总装顶层组件。");
            return document;
        }

        private static void CheckReferences(BaseInstallationState state, ISet<string> allowedDocuments, ISet<string> instances)
        {
            if (state == null) return;
            foreach (var component in new[] { state.Ordinary, state.Elevated }.Where(c => c != null))
                if (allowedDocuments == null || !allowedDocuments.Contains(component.Path) || !instances.Add(component.Instance))
                    throw Failure(component.Instance + "不属于已提交的基座引用范围或被重复归属。");
        }

        private static void CheckChain(BridgeModelState model, BaseInstallationState state, BaseInstallationChange change, ISet<string> consumed)
        {
            if (state == null) return;
            var bottom = state.Elevated ?? state.Ordinary;
            RequireMate(model, new BaseInstallationComponent { Instance = "", Coordinate = state.RootCoordinate }, bottom, consumed);
            if (state.Elevated != null)
                RequireMate(model, new BaseInstallationComponent { Instance = state.Elevated.Instance, Path = state.Elevated.Path,
                    Configuration = state.Elevated.Configuration, Coordinate = "上" }, state.Ordinary, consumed);
            if (change.FrameAnchor != null)
                RequireMate(model, new BaseInstallationComponent { Instance = state.Ordinary.Instance, Path = state.Ordinary.Path,
                    Configuration = state.Ordinary.Configuration, Coordinate = change.BridgeCoordinate }, change.FrameAnchor, consumed);
        }

        private static void RequireMate(BridgeModelState model, BaseInstallationComponent first, BaseInstallationComponent second, ISet<string> consumed)
        {
            var matches = model.Connections.Where(p => p.Value.Connection?.Endpoints?.Count == 2 &&
                (Matches(p.Value, p.Value.Connection.Endpoints[0], first) && Matches(p.Value, p.Value.Connection.Endpoints[1], second) ||
                 Matches(p.Value, p.Value.Connection.Endpoints[1], first) && Matches(p.Value, p.Value.Connection.Endpoints[0], second))).ToArray();
            var label = first.Instance + "/" + first.Coordinate + " ↔ " + second.Instance + "/" + second.Coordinate;
            if (matches.Length != 1) throw Failure(label + "安装配合缺失或重复。");
            var pair = matches[0];
            var connection = pair.Value.Connection;
            if (!consumed.Add(pair.Key) || connection.Type != (int)swMateType_e.swMateCOORDINATE ||
                // SW2022 将 AddMate5(swAlignNONE) 创建的坐标系配合反读为 CLOSEST；仍须核验实际三轴重合。
                connection.Alignment != (int)swMateAlign_e.swMateAlignCLOSEST || connection.Suppressed || connection.Error != 0 || connection.Warning ||
                pair.Value.Geometry.Count != 0 || pair.Value.Properties.TryGetValue("类型及对齐", out var type) &&
                    type != connection.Type + "/" + connection.Alignment)
                throw Failure(label + "配合类型、对齐、压缩或求解状态异常。");
            RequireCoincident(connection.Endpoints[0].WorldTransform, connection.Endpoints[1].WorldTransform, label);
        }

        private static bool Matches(BridgeModelDocumentState document, BridgeModelConnectionEndpoint endpoint, BaseInstallationComponent target) =>
            endpoint != null && endpoint.Kind == "CoordSys" && endpoint.Instance == target.Instance && endpoint.Reference == target.Coordinate &&
            endpoint.Configuration == (target.Instance == "" ? document.Configuration : target.Configuration);

        private static void RequireCoincident(double[] a, double[] b, string label)
        {
            if (a == null || b == null || a.Length != 16 || b.Length != 16 || a.Take(13).Concat(b.Take(13)).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                a[12] <= 0 || b[12] <= 0 || Math.Abs(a[12] - 1.0) > 1.0 || Math.Abs(b[12] - 1.0) > 1.0)
                throw Failure(label + "缺少有效的实际连接坐标变换。");
            var distanceMm = Math.Sqrt(Enumerable.Range(9, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i]))) * 1000.0;
            var maxAngle = 0.0;
            var angularConflict = false;
            for (var axis = 0; axis < 3; axis++)
            {
                var start = axis * 3;
                var lengthA = Math.Sqrt(Enumerable.Range(start, 3).Sum(i => a[i] * a[i]));
                var lengthB = Math.Sqrt(Enumerable.Range(start, 3).Sum(i => b[i] * b[i]));
                if (lengthA <= 0 || lengthB <= 0 || double.IsInfinity(lengthA) || double.IsInfinity(lengthB))
                    throw Failure(label + "连接坐标轴无效。");
                var cosine = Enumerable.Range(start, 3).Sum(i => a[i] * b[i]) / (lengthA * lengthB);
                // 直接比较余弦，避免 acos 的往返误差把恰好 1.0° 拒为超差；没有引入额外容差。
                angularConflict |= cosine < Math.Cos(1.0 * Math.PI / 180.0);
                maxAngle = Math.Max(maxAngle, Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosine))) * 180.0 / Math.PI);
            }
            if (distanceMm > 1.0 || angularConflict)
                throw Failure(label + "实际连接未重合，位置差" + distanceMm.ToString("F1", CultureInfo.InvariantCulture) +
                    " mm，角度差" + maxAngle.ToString("F1", CultureInfo.InvariantCulture) + "°。");
        }

        private static void RequireNoOtherConnections(BridgeModelState model, ISet<string> instances, ISet<string> consumed)
        {
            foreach (var pair in model.Connections)
                if (!consumed.Contains(pair.Key) && pair.Value.Connection.Endpoints.Any(e => instances.Contains((e.Instance ?? "").Split('/')[0])))
                    throw Failure(pair.Key + "包含基座事务未声明的额外安装连接。");
        }

        private static BridgeModelState Without(BridgeModelState state, ISet<string> consumed) => new BridgeModelState {
            Version = state.Version, AssemblyFileHash = state.AssemblyFileHash, FileHashes = state.FileHashes,
            Instances = state.Instances, Documents = state.Documents, Installations = state.Installations,
            Connections = state.Connections.Where(p => !consumed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };

        private static BaseInstallationChange[] Copy(BaseInstallationChange[] changes) =>
            JsonConvert.DeserializeObject<BaseInstallationChange[]>(JsonConvert.SerializeObject(changes),
                new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, ObjectCreationHandling = ObjectCreationHandling.Replace });

        private static InvalidOperationException Failure(string message) => new InvalidOperationException("基座安装转换核验失败：" + message);
    }
}
