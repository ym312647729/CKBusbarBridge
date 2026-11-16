using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 持久化用户最后一次手动设置的项目根目录。
    /// </summary>
    internal sealed class ProjectRootPreferenceStore
    {
        private const string PreferenceFolderName = "CKBusbarBridge";
        private const string PreferenceFileName = "project-root-preference.json";
        private readonly object _fileLock = new object();
        private readonly string _preferenceFilePath;

        internal ProjectRootPreferenceStore(string preferenceFilePath)
        {
            if (string.IsNullOrWhiteSpace(preferenceFilePath))
            {
                throw new ArgumentException("项目根目录设置文件路径不能为空。", nameof(preferenceFilePath));
            }

            _preferenceFilePath = Path.GetFullPath(preferenceFilePath);
        }

        internal static ProjectRootPreferenceStore Default { get; } =
            new ProjectRootPreferenceStore(ResolveDefaultPreferenceFilePath());

        internal string LoadExistingProjectRoot()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(_preferenceFilePath))
                    {
                        return null;
                    }

                    var preference = JsonConvert.DeserializeObject<ProjectRootPreference>(
                        File.ReadAllText(_preferenceFilePath, Encoding.UTF8));
                    if (preference == null ||
                        string.IsNullOrWhiteSpace(preference.ProjectRootPath))
                    {
                        return null;
                    }

                    var projectRootPath = Path.GetFullPath(
                        preference.ProjectRootPath.Trim());
                    return Directory.Exists(projectRootPath)
                        ? projectRootPath
                        : null;
                }
                catch
                {
                    // 设置损坏或旧路径不可解析时按未设置处理，不阻止窗口打开。
                    return null;
                }
            }
        }

        internal bool TrySave(string projectRootPath, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                if (string.IsNullOrWhiteSpace(projectRootPath))
                {
                    throw new ArgumentException("项目根目录不能为空。", nameof(projectRootPath));
                }

                var fullProjectRootPath = Path.GetFullPath(projectRootPath);
                if (!Directory.Exists(fullProjectRootPath))
                {
                    throw new DirectoryNotFoundException(
                        "项目根目录不存在：" + fullProjectRootPath);
                }

                lock (_fileLock)
                {
                    var preferenceDirectory = Path.GetDirectoryName(
                        _preferenceFilePath);
                    if (!string.IsNullOrWhiteSpace(preferenceDirectory))
                    {
                        Directory.CreateDirectory(preferenceDirectory);
                    }

                    File.WriteAllText(
                        _preferenceFilePath,
                        JsonConvert.SerializeObject(
                            new ProjectRootPreference
                            {
                                Version = 1,
                                ProjectRootPath = fullProjectRootPath
                            },
                            Formatting.Indented),
                        new UTF8Encoding(false));
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string ResolveDefaultPreferenceFilePath()
        {
            var localApplicationDataDirectory =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(
                localApplicationDataDirectory,
                PreferenceFolderName,
                PreferenceFileName);
        }

        private sealed class ProjectRootPreference
        {
            public int Version { get; set; }

            public string ProjectRootPath { get; set; }
        }
    }
}
