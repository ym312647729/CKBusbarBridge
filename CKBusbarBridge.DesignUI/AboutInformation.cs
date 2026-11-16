using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace CKBusbarBridge
{
    /// <summary>版本来自已加载的宿主，说明来自同一安装目录；不依赖当前工作目录。</summary>
    public sealed class AboutInformation
    {
        public const string ProductName = "川开中压母线桥 · 试用版";
        public const string ReleaseNotesFileName = "版本更新文档.md";

        private AboutInformation(string version, string notes, string message, Exception error = null)
        {
            Version = version;
            ReleaseNotes = notes;
            ReleaseNotesMessage = message;
            ReleaseNotesError = error;
        }

        public string Version { get; }
        public string ReleaseNotes { get; }
        public string ReleaseNotesMessage { get; }
        public Exception ReleaseNotesError { get; }
        public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

        public static AboutInformation Load(Assembly hostAssembly)
        {
            if (hostAssembly == null) throw new ArgumentNullException(nameof(hostAssembly));
            var version = hostAssembly.GetName().Version?.ToString() ?? "未知";
            var directory = Path.GetDirectoryName(hostAssembly.Location);
            if (string.IsNullOrWhiteSpace(directory))
                return new AboutInformation(version, null, "无法确定插件安装目录，暂时无法显示更新说明。");
            var path = Path.Combine(directory, ReleaseNotesFileName);
            try
            {
                var notes = File.ReadAllText(path, new UTF8Encoding(false, true));
                return string.IsNullOrWhiteSpace(notes)
                    ? new AboutInformation(version, null, "版本更新文档为空，请补齐更新说明后重新打开此窗口。")
                    : new AboutInformation(version, notes, "版本更新文档.md · 可选择并复制正文");
            }
            catch (FileNotFoundException error) { return Missing(version, error); }
            catch (DirectoryNotFoundException error) { return Missing(version, error); }
            catch (UnauthorizedAccessException error)
            {
                return new AboutInformation(version, null, "没有权限读取版本更新文档，请检查文件访问权限。", error);
            }
            catch (IOException error)
            {
                return new AboutInformation(version, null, "暂时无法读取版本更新文档，请检查文件是否被占用后重试。", error);
            }
            catch (DecoderFallbackException error)
            {
                return new AboutInformation(version, null, "版本更新文档编码无效，请将文件保存为UTF-8后重试。", error);
            }
        }

        private static AboutInformation Missing(string version, Exception error) =>
            new AboutInformation(version, null, "未找到版本更新文档.md，请补齐发布文件后重新打开此窗口。", error);
    }
}
