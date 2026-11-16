using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CKBusbarBridge.AlgorithmWorker
{
    /// <summary>Worker stdin/stdout 的单行 UTF-8 Json 编解码器。</summary>
    public static class LinkageRouteWorkerMessageCodec
    {
        private static readonly DataContractJsonSerializerSettings SerializerSettings =
            new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = int.MaxValue,
                UseSimpleDictionaryFormat = true
            };

        public static string Serialize(LinkageRouteWorkerMessageDto workerMessage)
        {
            if (workerMessage == null)
            {
                throw new ArgumentNullException(nameof(workerMessage));
            }

            using (var workerMessageStream = new MemoryStream())
            {
                CreateSerializer().WriteObject(workerMessageStream, workerMessage);
                return Encoding.UTF8.GetString(workerMessageStream.ToArray());
            }
        }

        public static LinkageRouteWorkerMessageDto Deserialize(string workerMessageJson)
        {
            if (string.IsNullOrWhiteSpace(workerMessageJson))
            {
                throw new InvalidDataException("算法 Worker 返回了空消息。");
            }

            var normalizedWorkerMessageJson = workerMessageJson.TrimStart('\uFEFF');
            const string decodedUtf8BomText = "\u00EF\u00BB\u00BF";
            if (normalizedWorkerMessageJson.StartsWith(
                decodedUtf8BomText,
                StringComparison.Ordinal))
            {
                normalizedWorkerMessageJson = normalizedWorkerMessageJson.Substring(
                    decodedUtf8BomText.Length);
            }

            using (var workerMessageStream = new MemoryStream(
                Encoding.UTF8.GetBytes(normalizedWorkerMessageJson)))
            {
                var workerMessage = CreateSerializer().ReadObject(workerMessageStream) as
                    LinkageRouteWorkerMessageDto;
                if (workerMessage == null)
                {
                    throw new InvalidDataException("算法 Worker 消息无法解析。");
                }

                return workerMessage;
            }
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(LinkageRouteWorkerMessageDto),
                SerializerSettings);
        }
    }
}
