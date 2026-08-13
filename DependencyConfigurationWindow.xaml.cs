using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace AutoCADAddInManager
{
    /// <summary>
    /// 显示插件目录文件树，并允许用户选择需要额外复制的资源文件。
    /// </summary>
    public partial class DependencyConfigurationWindow : Window
    {
        /// <summary>
        /// 文件树的根节点集合。
        /// </summary>
        private readonly ObservableCollection<DependencyFileNode> _rootNodes;

        /// <summary>
        /// 延迟执行文件树筛选，让搜索框先完成文本和选区重绘。
        /// </summary>
        private readonly DispatcherTimer _searchTimer;

        /// <summary>
        /// 初始化依赖文件配置窗口。
        /// </summary>
        /// <param name="dllPath">插件主 DLL 的完整路径。</param>
        /// <param name="selectedRelativePaths">此前保存的相对文件路径。</param>
        public DependencyConfigurationWindow(
            string dllPath,
            IEnumerable<string> selectedRelativePaths)
        {
            InitializeComponent();

            PluginFolder = Path.GetDirectoryName(Path.GetFullPath(dllPath));
            HashSet<string> selectedPaths = new HashSet<string>(
                selectedRelativePaths ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            DependencyFileNode rootNode = CreateDirectoryNode(
                new DirectoryInfo(PluginFolder),
                PluginFolder,
                null);
            ApplyInitialSelection(rootNode, selectedPaths);
            rootNode.IsExpanded = true;
            _rootNodes = new ObservableCollection<DependencyFileNode> { rootNode };

            _searchTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(150),
                DispatcherPriority.Background,
                SearchTimer_Tick,
                Dispatcher);
            _searchTimer.Stop();

            DataContext = this;
        }

        /// <summary>
        /// 获取插件所在目录，用于窗口顶部显示。
        /// </summary>
        public string PluginFolder { get; private set; }

        /// <summary>
        /// 获取文件树根节点。
        /// </summary>
        public ObservableCollection<DependencyFileNode> RootNodes
        {
            get { return _rootNodes; }
        }

        /// <summary>
        /// 获取用户最终选中的相对文件路径。
        /// </summary>
        public IEnumerable<string> SelectedRelativePaths
        {
            get
            {
                return EnumerateNodes(_rootNodes)
                    .Where(node => !node.IsDirectory && node.IsChecked == true)
                    .Select(node => node.RelativePath);
            }
        }

        /// <summary>
        /// 递归创建一个目录节点及其子目录、文件节点。
        /// </summary>
        /// <param name="directory">当前目录。</param>
        /// <param name="rootFolder">插件根目录。</param>
        /// <param name="parent">父节点。</param>
        /// <returns>构建完成的目录节点。</returns>
        private static DependencyFileNode CreateDirectoryNode(
            DirectoryInfo directory,
            string rootFolder,
            DependencyFileNode parent)
        {
            DependencyFileNode node = new DependencyFileNode(
                directory.Name,
                GetRelativePath(rootFolder, directory.FullName),
                true,
                parent);

            try
            {
                foreach (DirectoryInfo childDirectory in directory.GetDirectories()
                    .Where(item => (item.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    node.Children.Add(CreateDirectoryNode(childDirectory, rootFolder, node));
                }

                foreach (FileInfo file in directory.GetFiles()
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    node.Children.Add(new DependencyFileNode(
                        file.Name,
                        GetRelativePath(rootFolder, file.FullName),
                        false,
                        node));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 无权读取的目录仍显示节点，但不展开其内容。
            }
            catch (IOException)
            {
                // 文件系统状态变化时保留已经成功读取的节点。
            }

            return node;
        }

        /// <summary>
        /// 按保存的相对路径恢复文件勾选状态，并汇总目录的三态状态。
        /// </summary>
        /// <param name="node">当前节点。</param>
        /// <param name="selectedPaths">已保存的相对文件路径。</param>
        private static void ApplyInitialSelection(
            DependencyFileNode node,
            HashSet<string> selectedPaths)
        {
            if (!node.IsDirectory)
            {
                node.SetChecked(selectedPaths.Contains(node.RelativePath), false, false);
                return;
            }

            foreach (DependencyFileNode child in node.Children)
            {
                ApplyInitialSelection(child, selectedPaths);
            }

            node.RefreshFromChildren(false);
        }

        /// <summary>
        /// 深度优先枚举全部树节点。
        /// </summary>
        /// <param name="nodes">起始节点集合。</param>
        /// <returns>起始节点及其全部后代。</returns>
        private static IEnumerable<DependencyFileNode> EnumerateNodes(
            IEnumerable<DependencyFileNode> nodes)
        {
            foreach (DependencyFileNode node in nodes)
            {
                yield return node;
                foreach (DependencyFileNode child in EnumerateNodes(node.Children))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// 获取指定路径相对于插件目录的路径；根节点返回目录显示名。
        /// </summary>
        /// <param name="rootFolder">插件根目录。</param>
        /// <param name="path">文件或目录完整路径。</param>
        /// <returns>相对路径。</returns>
        private static string GetRelativePath(string rootFolder, string path)
        {
            string rootPath = Path.GetFullPath(rootFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(rootPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return fullPath.Substring(rootPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// 清空按钮事件，取消选择全部文件。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (DependencyFileNode rootNode in _rootNodes)
            {
                rootNode.IsChecked = false;
            }
        }

        /// <summary>
        /// 根据输入的文件名或相对路径即时筛选依赖文件树。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">文本变化事件参数。</param>
        private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // InitializeComponent 期间计时器尚未创建，不需要执行首次空文本筛选。
            if (_searchTimer == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(searchTextBox.Text))
            {
                // 清空文本时同步清除选区，避免繁重的树刷新留下选择色残影。
                searchTextBox.Select(0, 0);
            }

            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// <summary>
        /// 在用户暂时停止输入后执行文件树筛选。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">计时器事件参数。</param>
        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string searchText = searchTextBox.Text == null
                ? string.Empty
                : searchTextBox.Text.Trim();

            foreach (DependencyFileNode rootNode in _rootNodes)
            {
                ApplySearchFilter(rootNode, searchText);
            }
        }

        /// <summary>
        /// 递归设置节点可见性；目录自身或任一后代匹配时保留目录节点。
        /// </summary>
        /// <param name="node">当前文件树节点。</param>
        /// <param name="searchText">搜索关键字。</param>
        /// <returns>当前节点或其后代是否匹配。</returns>
        private static bool ApplySearchFilter(DependencyFileNode node, string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                node.Visibility = Visibility.Visible;
                foreach (DependencyFileNode child in node.Children)
                {
                    ApplySearchFilter(child, searchText);
                }

                return true;
            }

            bool nodeMatches = node.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                || node.RelativePath.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
            bool descendantMatches = false;
            foreach (DependencyFileNode child in node.Children)
            {
                descendantMatches |= ApplySearchFilter(child, searchText);
            }

            bool isVisible = nodeMatches || descendantMatches;
            node.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            if (node.IsDirectory && descendantMatches)
            {
                node.IsExpanded = true;
            }

            return isVisible;
        }

        /// <summary>
        /// 保存按钮事件，由调用方负责将选择结果写入配置文件。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        /// <summary>
        /// 表示依赖配置文件树中的目录或文件节点。
        /// </summary>
        public sealed class DependencyFileNode : INotifyPropertyChanged
        {
            private bool? _isChecked;
            private bool _isExpanded;
            private Visibility _visibility = Visibility.Visible;

            /// <summary>
            /// 初始化依赖文件树节点。
            /// </summary>
            /// <param name="name">节点显示名称。</param>
            /// <param name="relativePath">相对于插件目录的路径。</param>
            /// <param name="isDirectory">节点是否为目录。</param>
            /// <param name="parent">父节点。</param>
            public DependencyFileNode(
                string name,
                string relativePath,
                bool isDirectory,
                DependencyFileNode parent)
            {
                Name = name;
                RelativePath = relativePath;
                IsDirectory = isDirectory;
                Parent = parent;
                Children = new ObservableCollection<DependencyFileNode>();
                _isChecked = false;
            }

            /// <summary>
            /// 节点属性变化事件。
            /// </summary>
            public event PropertyChangedEventHandler PropertyChanged;

            /// <summary>
            /// 获取节点显示名称。
            /// </summary>
            public string Name { get; private set; }

            /// <summary>
            /// 获取节点相对于插件目录的路径。
            /// </summary>
            public string RelativePath { get; private set; }

            /// <summary>
            /// 获取节点是否表示目录。
            /// </summary>
            public bool IsDirectory { get; private set; }

            /// <summary>
            /// 获取父节点。
            /// </summary>
            public DependencyFileNode Parent { get; private set; }

            /// <summary>
            /// 获取直接子节点集合。
            /// </summary>
            public ObservableCollection<DependencyFileNode> Children { get; private set; }

            /// <summary>
            /// 获取或设置节点三态勾选值。
            /// </summary>
            public bool? IsChecked
            {
                get { return _isChecked; }
                set { SetChecked(value, true, true); }
            }

            /// <summary>
            /// 获取或设置目录节点是否展开。
            /// </summary>
            public bool IsExpanded
            {
                get { return _isExpanded; }
                set
                {
                    if (_isExpanded == value)
                    {
                        return;
                    }

                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }

            /// <summary>
            /// 获取或设置搜索筛选后的节点可见性。
            /// </summary>
            public Visibility Visibility
            {
                get { return _visibility; }
                set
                {
                    if (_visibility == value)
                    {
                        return;
                    }

                    _visibility = value;
                    OnPropertyChanged();
                }
            }

            /// <summary>
            /// 设置勾选状态，并按需同步子节点和父节点。
            /// </summary>
            /// <param name="value">新的三态勾选值。</param>
            /// <param name="updateChildren">是否同步所有子节点。</param>
            /// <param name="updateParent">是否重新计算父节点状态。</param>
            internal void SetChecked(bool? value, bool updateChildren, bool updateParent)
            {
                bool? normalizedValue = value ?? false;
                if (_isChecked != normalizedValue)
                {
                    _isChecked = normalizedValue;
                    OnPropertyChanged("IsChecked");
                }

                if (updateChildren && IsDirectory)
                {
                    foreach (DependencyFileNode child in Children)
                    {
                        child.SetChecked(normalizedValue, true, false);
                    }
                }

                if (updateParent && Parent != null)
                {
                    Parent.RefreshFromChildren(true);
                }
            }

            /// <summary>
            /// 根据所有直接子节点重新计算当前目录的三态勾选值。
            /// </summary>
            /// <param name="updateParent">是否继续更新上级目录。</param>
            internal void RefreshFromChildren(bool updateParent)
            {
                if (Children.Count == 0)
                {
                    SetChecked(false, false, updateParent);
                    return;
                }

                bool allChecked = Children.All(child => child.IsChecked == true);
                bool allUnchecked = Children.All(child => child.IsChecked == false);
                bool? value = allChecked ? true : allUnchecked ? (bool?)false : null;

                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged("IsChecked");
                }

                if (updateParent && Parent != null)
                {
                    Parent.RefreshFromChildren(true);
                }
            }

            /// <summary>
            /// 触发属性变化通知。
            /// </summary>
            /// <param name="propertyName">属性名称。</param>
            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
        }
    }
}
