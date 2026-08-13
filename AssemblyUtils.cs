using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AutoCADAddInManager
{
    /// <summary>
    /// 管理插件程序集的临时复制、加载和依赖解析。
    /// </summary>
    public class AssemblyUtils
    {
        /// <summary>
        /// 所有 AutoCADAddInManager 临时程序集的根目录。
        /// </summary>
        private static readonly string _temporaryRootFolder = Path.Combine(
            Path.GetTempPath(),
            "AutoCADAddInManager");

        /// <summary>
        /// 当前 CAD 进程使用的临时会话目录。
        /// </summary>
        private static readonly string _temporarySessionFolder = Path.Combine(
            _temporaryRootFolder,
            System.Diagnostics.Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// 同步程序集缓存和复制上下文的访问。
        /// </summary>
        private static readonly object _syncRoot = new object();

        /// <summary>
        /// 按临时文件路径缓存已加载的程序集，防止同一依赖重复加载。
        /// </summary>
        private static readonly Dictionary<string, Assembly> _loadedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 缓存当前命令扫描过程中从字节加载的主程序集和依赖程序集。
        /// </summary>
        private static readonly Dictionary<string, Assembly> _memoryParsingAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 当前从内存扫描命令的 DLL 原目录。
        /// </summary>
        private static string _memoryParsingFolder;

        /// <summary>
        /// 指示当前是否正在界面线程中扫描内存程序集的命令信息。
        /// </summary>
        private static bool _memoryParsingActive;

        /// <summary>
        /// 保存当前进程创建的全部复制上下文，用于按请求程序集定位依赖目录。
        /// </summary>
        private static readonly List<TemporaryCopyContext> _copyContexts =
            new List<TemporaryCopyContext>();

        /// <summary>
        /// 当前正在执行的插件复制上下文。
        /// </summary>
        private static TemporaryCopyContext _currentCopyContext;

        /// <summary>
        /// 初始化程序集解析器并清理以前运行产生的临时文件。
        /// </summary>
        public static void Initialize()
        {
            CleanupTemporaryAssemblies();
            AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            lock (_syncRoot)
            {
                _loadedAssemblies.Clear();
                _memoryParsingAssemblies.Clear();
                _memoryParsingFolder = null;
                _memoryParsingActive = false;
                _copyContexts.Clear();
                _currentCopyContext = null;
            }
        }

        /// <summary>
        /// 加载用于执行命令的程序集，只复制默认依赖和用户配置的附加文件。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <returns>从临时目录加载的程序集。</returns>
        public static Assembly LoadAssembly(string dllPath)
        {
            return LoadAssemblyInternal(dllPath);
        }

        /// <summary>
        /// 从 DLL 和 PDB 字节加载用于读取命令信息的程序集，不创建临时副本。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <returns>从内存加载的程序集。</returns>
        public static Assembly LoadAssemblyForParsing(string dllPath)
        {
            string fullPath = Path.GetFullPath(dllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到待解析的插件 DLL。", fullPath);
            }

            lock (_syncRoot)
            {
                _memoryParsingFolder = Path.GetDirectoryName(fullPath);
                _memoryParsingAssemblies.Clear();
                _memoryParsingActive = true;

                Assembly assembly = LoadAssemblyBytes(fullPath);
                _memoryParsingAssemblies.Add(fullPath, assembly);
                return assembly;
            }
        }

        /// <summary>
        /// 结束当前命令扫描并释放内存解析器保存的程序集引用。
        /// </summary>
        internal static void EndAssemblyParsing()
        {
            lock (_syncRoot)
            {
                _memoryParsingActive = false;
                _memoryParsingFolder = null;
                _memoryParsingAssemblies.Clear();
            }
        }

        /// <summary>
        /// 在独立临时目录中准备并加载插件程序集。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <returns>从临时目录加载的程序集。</returns>
        private static Assembly LoadAssemblyInternal(string dllPath)
        {
            string fullPath = Path.GetFullPath(dllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到待加载的插件 DLL。", fullPath);
            }

            string originalFolder = Path.GetDirectoryName(fullPath);
            string folderName = Path.GetFileNameWithoutExtension(fullPath)
                + "-Executing-" + Guid.NewGuid().ToString("N");
            string temporaryFolder = Path.Combine(_temporarySessionFolder, folderName);
            TemporaryCopyContext context = new TemporaryCopyContext(
                originalFolder,
                temporaryFolder);

            lock (_syncRoot)
            {
                _memoryParsingFolder = null;
                _memoryParsingAssemblies.Clear();
                _memoryParsingActive = false;
            }

            Directory.CreateDirectory(temporaryFolder);
            CopyRelatedFiles(fullPath, context);
            CopyConfiguredFiles(fullPath, context);

            string temporaryDllPath = Path.Combine(temporaryFolder, Path.GetFileName(fullPath));
            if (!File.Exists(temporaryDllPath))
            {
                throw new IOException("无法将插件主 DLL 复制到临时目录：" + temporaryDllPath);
            }

            lock (_syncRoot)
            {
                _currentCopyContext = context;
                _copyContexts.Add(context);
            }

            Assembly assembly = LoadAssemblyFile(temporaryDllPath);
            context.MainAssembly = assembly;
            return assembly;
        }

        /// <summary>
        /// 复制源 DLL 及目录中与其同名的 PDB、XML、JSON 等相关文件。
        /// </summary>
        /// <param name="sourceDllPath">源 DLL 路径。</param>
        /// <param name="context">目标复制上下文。</param>
        private static void CopyRelatedFiles(string sourceDllPath, TemporaryCopyContext context)
        {
            string sourceFolder = Path.GetDirectoryName(sourceDllPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceDllPath);
            foreach (string sourcePath in Directory.GetFiles(
                sourceFolder,
                fileNameWithoutExtension + ".*",
                SearchOption.TopDirectoryOnly))
            {
                string destinationPath = Path.Combine(context.TemporaryFolder, Path.GetFileName(sourcePath));
                CopyFile(sourcePath, destinationPath);
            }
        }

        /// <summary>
        /// 复制用户为当前插件勾选的附加文件，并保持相对于插件目录的结构。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <param name="context">当前执行复制上下文。</param>
        private static void CopyConfiguredFiles(string dllPath, TemporaryCopyContext context)
        {
            foreach (string configuredPath in DependencyConfigurationStore.LoadSelectedFiles(dllPath))
            {
                try
                {
                    string sourcePath = Path.GetFullPath(Path.Combine(context.OriginalFolder, configuredPath));
                    if (!IsPathInsideFolder(sourcePath, context.OriginalFolder) || !File.Exists(sourcePath))
                    {
                        continue;
                    }

                    string relativePath = GetRelativePath(context.OriginalFolder, sourcePath);
                    string destinationPath = Path.Combine(context.TemporaryFolder, relativePath);
                    CopyFile(sourcePath, destinationPath);
                }
                catch (ArgumentException)
                {
                    // 配置中的非法相对路径直接忽略，其他有效配置仍可继续复制。
                }
                catch (NotSupportedException)
                {
                    // 不支持的路径格式直接忽略。
                }
                catch (PathTooLongException)
                {
                    // 超出系统长度限制的文件无法复制，跳过该项。
                }
                catch (IOException)
                {
                    // 文件被占用或复制失败时跳过该配置项。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权读取的配置文件直接跳过。
                }
            }
        }

        /// <summary>
        /// 复制单个文件，并在需要时创建目标目录、清除目标只读属性。
        /// </summary>
        /// <param name="sourcePath">源文件路径。</param>
        /// <param name="destinationPath">目标文件路径。</param>
        private static void CopyFile(string sourcePath, string destinationPath)
        {
            string destinationFolder = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(destinationFolder);
            if (File.Exists(destinationPath))
            {
                File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) & ~FileAttributes.ReadOnly);
            }

            File.Copy(sourcePath, destinationPath, true);
            File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) & ~FileAttributes.ReadOnly);
        }

        /// <summary>
        /// 读取程序集及同名 PDB 字节，用于不锁定原文件的命令扫描。
        /// </summary>
        /// <param name="assemblyPath">程序集完整路径。</param>
        /// <returns>从字节加载的程序集。</returns>
        private static Assembly LoadAssemblyBytes(string assemblyPath)
        {
            byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath))
            {
                return Assembly.Load(assemblyBytes);
            }

            byte[] pdbBytes = File.ReadAllBytes(pdbPath);
            return Assembly.Load(assemblyBytes, pdbBytes);
        }

        /// <summary>
        /// 尝试为当前内存扫描程序集从原目录按字节解析依赖。
        /// </summary>
        /// <param name="requestingAssembly">发起依赖请求的程序集。</param>
        /// <param name="assemblyName">依赖程序集简单名称。</param>
        /// <param name="resolvedAssembly">解析到的程序集；未找到时为 <see langword="null"/>。</param>
        /// <returns>请求来自当前内存扫描上下文时返回 <see langword="true"/>。</returns>
        private static bool TryResolveMemoryParsingAssembly(
            Assembly requestingAssembly,
            string assemblyName,
            out Assembly resolvedAssembly)
        {
            resolvedAssembly = null;
            lock (_syncRoot)
            {
                bool belongsToMemoryParsing = _memoryParsingActive
                    && (requestingAssembly == null
                        || _memoryParsingAssemblies.Values.Any(assembly =>
                            ReferenceEquals(assembly, requestingAssembly)));
                if (!belongsToMemoryParsing || string.IsNullOrEmpty(_memoryParsingFolder))
                {
                    return false;
                }

                string dependencyPath = FindAssemblyFile(
                    _memoryParsingFolder,
                    assemblyName,
                    false);
                if (string.IsNullOrEmpty(dependencyPath))
                {
                    return true;
                }

                if (_memoryParsingAssemblies.TryGetValue(dependencyPath, out resolvedAssembly))
                {
                    return true;
                }

                resolvedAssembly = LoadAssemblyBytes(dependencyPath);
                _memoryParsingAssemblies.Add(dependencyPath, resolvedAssembly);
                return true;
            }
        }

        /// <summary>
        /// 从临时路径加载程序集，并复用同一路径已经加载的依赖程序集。
        /// </summary>
        /// <param name="temporaryAssemblyPath">临时程序集完整路径。</param>
        /// <returns>加载完成的程序集。</returns>
        private static Assembly LoadAssemblyFile(string temporaryAssemblyPath)
        {
            string fullPath = Path.GetFullPath(temporaryAssemblyPath);
            lock (_syncRoot)
            {
                Assembly cachedAssembly;
                if (_loadedAssemblies.TryGetValue(fullPath, out cachedAssembly))
                {
                    return cachedAssembly;
                }

                Assembly assembly = Assembly.LoadFile(fullPath);
                _loadedAssemblies.Add(fullPath, assembly);
                return assembly;
            }
        }

        /// <summary>
        /// 处理插件依赖解析，优先加载临时目录中的副本。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="args">程序集解析参数。</param>
        /// <returns>解析到的程序集；未找到时返回 <see langword="null"/>。</returns>
        public static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requestedAssemblyName = new AssemblyName(args.Name);
                string assemblyName = requestedAssemblyName.Name;
                if (string.IsNullOrEmpty(assemblyName) || assemblyName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                Assembly memoryParsingAssembly;
                if (TryResolveMemoryParsingAssembly(
                    args.RequestingAssembly,
                    assemblyName,
                    out memoryParsingAssembly))
                {
                    return memoryParsingAssembly;
                }

                TemporaryCopyContext context = GetCopyContext(args.RequestingAssembly);
                if (context == null)
                {
                    return null;
                }

                string temporaryAssemblyPath = FindAssemblyFile(
                    context.TemporaryFolder,
                    assemblyName,
                    true);
                if (!string.IsNullOrEmpty(temporaryAssemblyPath))
                {
                    return LoadAssemblyFile(temporaryAssemblyPath);
                }

                string originalAssemblyPath = FindAssemblyFile(
                    context.OriginalFolder,
                    assemblyName,
                    false);
                if (string.IsNullOrEmpty(originalAssemblyPath))
                {
                    return null;
                }

                // 解析阶段没有复制完整目录，按需复制依赖及其同名相关文件。
                CopyRelatedFiles(originalAssemblyPath, context);
                temporaryAssemblyPath = FindAssemblyFile(
                    context.TemporaryFolder,
                    assemblyName,
                    true);
                return string.IsNullOrEmpty(temporaryAssemblyPath)
                    ? null
                    : LoadAssemblyFile(temporaryAssemblyPath);
            }
            catch
            {
                // 解析失败时交还 CLR 继续处理，避免影响宿主程序中的其他程序集。
                return null;
            }
        }

        /// <summary>
        /// 按简单程序集名称在指定目录查找 DLL 或 EXE。
        /// </summary>
        /// <param name="folder">搜索目录。</param>
        /// <param name="assemblyName">程序集简单名称。</param>
        /// <param name="searchSubdirectories">是否递归搜索子目录。</param>
        /// <returns>匹配文件的完整路径；未找到时返回 <see langword="null"/>。</returns>
        private static string FindAssemblyFile(
            string folder,
            string assemblyName,
            bool searchSubdirectories)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            string dllPath = Path.Combine(folder, assemblyName + ".dll");
            if (File.Exists(dllPath))
            {
                return dllPath;
            }

            string exePath = Path.Combine(folder, assemblyName + ".exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            if (!searchSubdirectories)
            {
                return null;
            }

            try
            {
                string recursiveDllPath = Directory.GetFiles(
                    folder,
                    assemblyName + ".dll",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(recursiveDllPath))
                {
                    return recursiveDllPath;
                }

                return Directory.GetFiles(
                    folder,
                    assemblyName + ".exe",
                    SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// 根据请求程序集查找其所属复制上下文。
        /// </summary>
        /// <param name="requestingAssembly">发起依赖解析的程序集。</param>
        /// <returns>匹配的复制上下文；无法匹配时返回当前上下文。</returns>
        private static TemporaryCopyContext GetCopyContext(Assembly requestingAssembly)
        {
            lock (_syncRoot)
            {
                if (requestingAssembly != null)
                {
                    foreach (TemporaryCopyContext context in _copyContexts)
                    {
                        if (ReferenceEquals(context.MainAssembly, requestingAssembly))
                        {
                            return context;
                        }
                    }

                    string requestingLocation = requestingAssembly.Location;
                    if (!string.IsNullOrEmpty(requestingLocation))
                    {
                        string fullLocation = Path.GetFullPath(requestingLocation);
                        foreach (TemporaryCopyContext context in _copyContexts)
                        {
                            if (IsPathInsideFolder(fullLocation, context.TemporaryFolder))
                            {
                                return context;
                            }
                        }
                    }
                }

                return _currentCopyContext;
            }
        }

        /// <summary>
        /// 判断文件路径是否位于指定目录内。
        /// </summary>
        /// <param name="path">待检查的完整路径。</param>
        /// <param name="folder">父目录路径。</param>
        /// <returns>文件位于目录内时返回 <see langword="true"/>。</returns>
        private static bool IsPathInsideFolder(string path, string folder)
        {
            string folderPrefix = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取文件或目录相对于基准目录的路径。
        /// </summary>
        /// <param name="baseFolder">基准目录。</param>
        /// <param name="path">目标完整路径。</param>
        /// <returns>不以目录分隔符开头的相对路径。</returns>
        private static string GetRelativePath(string baseFolder, string path)
        {
            string basePrefix = Path.GetFullPath(baseFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.Substring(basePrefix.Length);
        }

        /// <summary>
        /// 清理以前运行产生的临时程序集；被其他进程占用的文件和非空目录将被保留。
        /// </summary>
        private static void CleanupTemporaryAssemblies()
        {
            if (!Directory.Exists(_temporaryRootFolder))
            {
                return;
            }

            string currentSessionPrefix = Path.GetFullPath(_temporarySessionFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string[] files;
            try
            {
                files = Directory.GetFiles(_temporaryRootFolder, "*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (string filePath in files)
            {
                string fullFilePath = Path.GetFullPath(filePath);
                if (fullFilePath.StartsWith(currentSessionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(fullFilePath);
                }
                catch (IOException)
                {
                    // 其他 CAD 进程仍在使用该临时程序集时保留文件。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权删除的文件不应影响 AutoCADAddInManager 启动。
                }
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(_temporaryRootFolder, "*", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            // 从最深层开始删除空目录，包含占用文件的目录会删除失败并被保留。
            foreach (string directoryPath in directories.OrderByDescending(path => path.Length))
            {
                string fullDirectoryPath = Path.GetFullPath(directoryPath);
                string directoryPrefix = fullDirectoryPath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (directoryPrefix.StartsWith(currentSessionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(fullDirectoryPath, false);
                }
                catch (IOException)
                {
                    // 目录仍含有占用文件时保留目录。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权删除的目录不应影响 AutoCADAddInManager 启动。
                }
            }
        }

        /// <summary>
        /// 表示一次插件执行使用的临时复制上下文。
        /// </summary>
        private sealed class TemporaryCopyContext
        {
            /// <summary>
            /// 初始化临时复制上下文。
            /// </summary>
            /// <param name="originalFolder">插件原目录。</param>
            /// <param name="temporaryFolder">插件临时目录。</param>
            public TemporaryCopyContext(string originalFolder, string temporaryFolder)
            {
                OriginalFolder = originalFolder;
                TemporaryFolder = temporaryFolder;
            }

            /// <summary>
            /// 获取插件原目录。
            /// </summary>
            public string OriginalFolder { get; private set; }

            /// <summary>
            /// 获取插件临时目录。
            /// </summary>
            public string TemporaryFolder { get; private set; }

            /// <summary>
            /// 获取或设置当前上下文加载的主程序集。
            /// </summary>
            public Assembly MainAssembly { get; set; }
        }
    }
}
