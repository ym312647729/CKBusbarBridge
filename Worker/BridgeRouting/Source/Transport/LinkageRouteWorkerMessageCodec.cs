using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RouteLinkBar.Algorithm.Transport
{
    /// <summary>将 Worker 消息编码为一行 UTF-8 Json，stdout 中不得混入其他文本。</summary>
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

            using (var messageStream = new MemoryStream())
            {
                CreateMessageSerializer().WriteObject(messageStream, workerMessage);
                return Encoding.UTF8.GetString(messageStream.ToArray());
            }
        }

        public static LinkageRouteWorkerMessageDto Deserialize(string messageJson)
        {
            if (string.IsNullOrWhiteSpace(messageJson))
            {
                throw new InvalidDataException("算法 Worker 返回了空消息。");
            }

            var normalizedMessageJson = NormalizeOptionalUtf8Bom(messageJson);

            using (var messageStream = new MemoryStream(
                Encoding.UTF8.GetBytes(normalizedMessageJson)))
            {
                var workerMessage = CreateMessageSerializer().ReadObject(messageStream) as
                    LinkageRouteWorkerMessageDto;
                if (workerMessage == null)
                {
                    throw new InvalidDataException("算法 Worker 消息无法解析。");
                }

                return workerMessage;
            }
        }

        /// <summary>将机器可读的 Worker 版本与更新日志编码为 UTF-8 Json。</summary>
        public static string SerializeWorkerInformation(
            LinkageRouteWorkerInformationDto workerInformation)
        {
            if (workerInformation == null)
            {
                throw new ArgumentNullException(nameof(workerInformation));
            }

            using (var workerInformationStream = new MemoryStream())
            {
                CreateWorkerInformationSerializer().WriteObject(
                    workerInformationStream,
                    workerInformation);
                return Encoding.UTF8.GetString(workerInformationStream.ToArray());
            }
        }

        /// <summary>从 UTF-8 Json 读取机器可读的 Worker 版本与更新日志。</summary>
        public static LinkageRouteWorkerInformationDto DeserializeWorkerInformation(
            string workerInformationJson)
        {
            if (string.IsNullOrWhiteSpace(workerInformationJson))
            {
                throw new InvalidDataException("算法 Worker 更新日志为空。");
            }

            var normalizedWorkerInformationJson = NormalizeOptionalUtf8Bom(
                workerInformationJson);
            using (var workerInformationStream = new MemoryStream(
                Encoding.UTF8.GetBytes(normalizedWorkerInformationJson)))
            {
                var decodedWorkerInformation = CreateWorkerInformationSerializer()
                    .ReadObject(workerInformationStream) as
                    LinkageRouteWorkerInformationDto;
                if (decodedWorkerInformation == null)
                {
                    throw new InvalidDataException("算法 Worker 更新日志无法解析。");
                }

                return decodedWorkerInformation;
            }
        }

        private static string NormalizeOptionalUtf8Bom(string sourceJsonText)
        {
            var normalizedJsonText = sourceJsonText.TrimStart('\uFEFF');
            const string decodedUtf8BomText = "\u00EF\u00BB\u00BF";
            if (normalizedJsonText.StartsWith(
                decodedUtf8BomText,
                StringComparison.Ordinal))
            {
                normalizedJsonText = normalizedJsonText.Substring(
                    decodedUtf8BomText.Length);
            }

            return normalizedJsonText;
        }

        private static DataContractJsonSerializer CreateMessageSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(LinkageRouteWorkerMessageDto),
                SerializerSettings);
        }

        private static DataContractJsonSerializer CreateWorkerInformationSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(LinkageRouteWorkerInformationDto),
                SerializerSettings);
        }
    }
}
