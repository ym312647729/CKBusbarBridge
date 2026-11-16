using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RD.WebPreview.Contracts
{
    /// <summary>通用预览协议和场景的 UTF-8 Json 编解码器。</summary>
    public static class WebPreviewWorkerMessageCodec
    {
        private static readonly DataContractJsonSerializerSettings SerializerSettings =
            new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = int.MaxValue,
                UseSimpleDictionaryFormat = true
            };

        public static string SerializeMessage(WebPreviewWorkerMessageDto workerMessage)
        {
            return SerializeObject(workerMessage);
        }

        public static WebPreviewWorkerMessageDto DeserializeMessage(string messageJson)
        {
            return DeserializeObject<WebPreviewWorkerMessageDto>(messageJson);
        }

        public static string SerializeScene(WebPreviewSceneDto previewScene)
        {
            return SerializeObject(previewScene);
        }

        public static WebPreviewSceneDto DeserializeScene(string sceneJson)
        {
            return DeserializeObject<WebPreviewSceneDto>(sceneJson);
        }

        private static string SerializeObject<TValue>(TValue serializableValue)
        {
            if (serializableValue == null)
            {
                throw new ArgumentNullException(nameof(serializableValue));
            }

            using (var serializedValueStream = new MemoryStream())
            {
                CreateSerializer<TValue>().WriteObject(
                    serializedValueStream,
                    serializableValue);
                return Encoding.UTF8.GetString(serializedValueStream.ToArray());
            }
        }

        private static TValue DeserializeObject<TValue>(string serializedJson)
        {
            if (string.IsNullOrWhiteSpace(serializedJson))
            {
                throw new InvalidDataException("Web 预览协议包含空 Json。" );
            }

            var normalizedJson = serializedJson.TrimStart('\uFEFF');
            const string decodedUtf8BomText = "\u00EF\u00BB\u00BF";
            if (normalizedJson.StartsWith(decodedUtf8BomText, StringComparison.Ordinal))
            {
                normalizedJson = normalizedJson.Substring(decodedUtf8BomText.Length);
            }

            using (var serializedValueStream = new MemoryStream(
                Encoding.UTF8.GetBytes(normalizedJson)))
            {
                var deserializedValue = CreateSerializer<TValue>().ReadObject(
                    serializedValueStream);
                if (deserializedValue == null)
                {
                    throw new InvalidDataException("Web 预览 Json 无法解析。" );
                }

                return (TValue)deserializedValue;
            }
        }

        private static DataContractJsonSerializer CreateSerializer<TValue>()
        {
            return new DataContractJsonSerializer(typeof(TValue), SerializerSettings);
        }
    }
}
