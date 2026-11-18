using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CKBusbarBridge.Computation;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    // The SW side stores only immutable display meshes. Calculation lives in Worker.
    internal sealed class BridgeGeometryRuntime : IDisposable
    {
        private readonly PreviewCalculator _calculator;
        private readonly Dictionary<string, BridgeMeshShape> _shapes = new Dictionary<string, BridgeMeshShape>();
        private readonly LinkedList<string> _shapeUsage = new LinkedList<string>();
        private const long MaximumShapeBytes = 192L * 1024 * 1024;
        private long _shapeBytes;
        private static string WorkerDirectory => Path.Combine(Path.GetDirectoryName(typeof(BridgeGeometryRuntime).Assembly.Location), "Worker");
        private static string RuntimeDirectory => Path.Combine(WorkerDirectory, "PreviewRuntime");
        internal static string PackageVersion
        {
            get { var manifest = JObject.Parse(File.ReadAllText(Path.Combine(RuntimeDirectory, "runtime-manifest.json")));
                return (string)manifest["calculatorVersion"] + ":" + (string)manifest["packageSha256"]; }
        }
        internal BridgeGeometryRuntime()
        {
            _calculator = new PreviewCalculator(WorkerDirectory);
        }
        internal async Task<JObject> Compute(JObject request, CancellationToken cancellationToken = default)
        {
            request["knownShapes"] = JObject.FromObject(_shapes.ToDictionary(pair => pair.Key, pair => pair.Value.Signature));
            // 视口使用明确的实例身份、外观和源路径，原始逐面来源仍留在计算与检查侧。
            request["includePartSources"] = false;
            var response = await _calculator.ComputeAsync(request, cancellationToken).ConfigureAwait(false);
            if ((string)response["calculatorVersion"] != PackageVersion) throw new InvalidOperationException("计算结果与 Worker 版本不匹配。");
            return response;
        }

        internal BridgeMeshFrame CreateFrame(Guid id, long revision, JObject response)
        {
            var available = new Dictionary<string, BridgeMeshShape>(_shapes);
            Func<JObject, BridgeMeshInstance> parse = value =>
            {
                var key = (string)value["geometryKey"]; var signature = (string)value["geometrySignature"];
                if (!available.TryGetValue(key, out var shape) || shape.Signature != signature)
                {
                    if (value["positions"] == null) throw new InvalidOperationException("计算器引用了已释放或签名不匹配的网格。");
                    var created = new BridgeMeshShape(value);
                    if (_shapes.TryGetValue(key, out var replaced)) { _shapeBytes -= replaced.Bytes; _shapes.Remove(key); _shapeUsage.Remove(key); }
                    while (_shapeBytes + created.Bytes > MaximumShapeBytes && _shapeUsage.First != null)
                    { var expired = _shapeUsage.First.Value; _shapeUsage.RemoveFirst(); _shapeBytes -= _shapes[expired].Bytes; _shapes.Remove(expired); }
                    if (created.Bytes <= MaximumShapeBytes) { _shapes.Add(key, created); _shapeBytes += created.Bytes; }
                    shape = created;
                }
                available[key] = shape;
                _shapeUsage.Remove(key); if (_shapes.ContainsKey(key)) _shapeUsage.AddLast(key);
                return new BridgeMeshInstance(value, shape);
            };
            var instances = response["instances"].OfType<JObject>().Select(parse).ToArray();
            var interaction = response["interactionInstances"]?.OfType<JObject>().Select(parse).ToArray();
            return new BridgeMeshFrame(id, revision, instances, (string)response["warning"], interaction);
        }

        public void Dispose()
        {
            _calculator.Dispose(); _shapes.Clear(); _shapeUsage.Clear(); _shapeBytes = 0;
        }
    }
}
