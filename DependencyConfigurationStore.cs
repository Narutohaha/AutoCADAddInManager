using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AutoCADAddInManager
{
    /// <summary>
    /// 持久化每个插件额外需要复制到临时目录的文件列表。
    /// </summary>
    internal static class DependencyConfigurationStore
    {
        /// <summary>
        /// 同步配置文件的读取和写入。
        /// </summary>
        private static readonly object _syncRoot = new object();

        /// <summary>
        /// 依赖文件配置路径，与 DLL 历史配置位于同一个目录。
        /// </summary>
        private static readonly string _configurationFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoCADAddInManager",
            "DependencyFiles.xml");

        /// <summary>
        /// 获取指定 DLL 已配置的相对文件路径。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <returns>使用不区分大小写规则的相对路径集合。</returns>
        internal static HashSet<string> LoadSelectedFiles(string dllPath)
        {
            string fullDllPath = Path.GetFullPath(dllPath);
            lock (_syncRoot)
            {
                try
                {
                    XDocument document = LoadDocument();
                    XElement dllElement = document.Root == null
                        ? null
                        : document.Root.Elements("Dll").FirstOrDefault(element =>
                            string.Equals(
                                (string)element.Attribute("Path"),
                                fullDllPath,
                                StringComparison.OrdinalIgnoreCase));
                    if (dllElement == null)
                    {
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    return new HashSet<string>(
                        dllElement.Elements("File")
                            .Select(element => (string)element.Attribute("Path"))
                            .Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // 配置损坏不应影响插件命令的默认加载。
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// 保存指定 DLL 选择的相对文件路径，同时保留其他 DLL 的配置。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <param name="relativePaths">用户选择的相对文件路径。</param>
        internal static void SaveSelectedFiles(string dllPath, IEnumerable<string> relativePaths)
        {
            string fullDllPath = Path.GetFullPath(dllPath);
            List<string> selectedPaths = relativePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_syncRoot)
            {
                XDocument document = LoadDocument();
                if (document.Root == null)
                {
                    document.Add(new XElement("DependencyConfigurations"));
                }

                foreach (XElement existingElement in document.Root.Elements("Dll")
                    .Where(element => string.Equals(
                        (string)element.Attribute("Path"),
                        fullDllPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    existingElement.Remove();
                }

                if (selectedPaths.Count > 0)
                {
                    document.Root.Add(new XElement(
                        "Dll",
                        new XAttribute("Path", fullDllPath),
                        selectedPaths.Select(path => new XElement(
                            "File",
                            new XAttribute("Path", path)))));
                }

                string configurationFolder = Path.GetDirectoryName(_configurationFilePath);
                Directory.CreateDirectory(configurationFolder);
                document.Save(_configurationFilePath);
            }
        }

        /// <summary>
        /// 读取配置文档；文件不存在或无法解析时返回新的空文档。
        /// </summary>
        /// <returns>依赖文件配置文档。</returns>
        private static XDocument LoadDocument()
        {
            if (!File.Exists(_configurationFilePath))
            {
                return new XDocument(new XElement("DependencyConfigurations"));
            }

            try
            {
                return XDocument.Load(_configurationFilePath);
            }
            catch
            {
                return new XDocument(new XElement("DependencyConfigurations"));
            }
        }
    }
}
