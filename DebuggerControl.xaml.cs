using Autodesk.AutoCAD.Runtime;
using CadAddinManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Path = System.IO.Path;
using UserControl = System.Windows.Controls.UserControl;

namespace AutoCADAddInManager
{
    /// <summary>
    /// 提供 DLL 命令浏览、历史记录和运行功能的调试控件。
    /// </summary>
    public partial class DebuggerControl : UserControl
    {
        /// <summary>
        /// 保存已加载 DLL 历史记录的配置文件路径。
        /// </summary>
        private static readonly string HistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoCADAddInManager",
            "LoadedDlls.xml");

        /// <summary>
        /// 当前通过 Ctrl 或 Shift 选中的命令节点集合。
        /// </summary>
        private readonly HashSet<TreeViewItem> selectedCommandNodes = new HashSet<TreeViewItem>();

        /// <summary>
        /// Shift 连续选择所使用的起始命令节点。
        /// </summary>
        private TreeViewItem commandSelectionAnchor;

        /// <summary>
        /// 标记正在由多选逻辑更新 WPF 的活动节点，避免选择事件重复处理。
        /// </summary>
        private bool updatingCommandSelection;

        /// <summary>
        /// 多选命令节点使用的柔和背景色。
        /// </summary>
        private static readonly Brush CommandSelectionBackground =
            new SolidColorBrush(Color.FromRgb(232, 237, 243));

        /// <summary>
        /// 多选命令节点使用的文字颜色。
        /// </summary>
        private static readonly Brush CommandSelectionForeground =
            new SolidColorBrush(Color.FromRgb(51, 65, 85));

        /// <summary>
        /// 初始化调试控件并恢复之前加载过的 DLL。
        /// </summary>
        public DebuggerControl()
        {
            InitializeComponent();
            btnLoad.Click += BtnLoad_Click;
            btnRemove.Click += BtnRemove_Click;
            btnRun.Click += BtnRun_Click;
            btnReloadDll.Click += BtnReloadDll_Click;
            btnConfigureDependencies.Click += BtnConfigureDependencies_Click;
            treeCommands.SelectedItemChanged += TreeCommands_SelectedItemChanged;
            treeCommands.PreviewMouseLeftButtonDown += TreeCommands_PreviewMouseLeftButtonDown;

            RestoreDllHistory();
        }

        /// <summary>
        /// 浏览 DLL，并把新 DLL 添加到树中；重复选择已有 DLL 时刷新其命令。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void BtnLoad_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "CAD 插件 (*.dll)|*.dll";

                // 优先使用当前选中 DLL 所在的目录，减少重复定位文件的操作。
                string selectedDllPath = GetSelectedDllPath();
                if (!string.IsNullOrEmpty(selectedDllPath))
                {
                    string directory = Path.GetDirectoryName(selectedDllPath);
                    if (Directory.Exists(directory))
                    {
                        ofd.InitialDirectory = directory;
                    }

                    ofd.FileName = Path.GetFileName(selectedDllPath);
                }

                if (ofd.ShowDialog() == DialogResult.OK && ReloadCommands(ofd.FileName, true))
                {
                    SaveDllHistory();
                }
            }
        }

        /// <summary>
        /// 解析 DLL 中的 CommandMethod，并添加或刷新对应的树节点。
        /// </summary>
        /// <param name="dllPath">DLL 的完整路径。</param>
        /// <param name="showError">解析失败时是否显示错误消息。</param>
        /// <returns>成功解析并更新树节点时返回 <see langword="true"/>。</returns>
        private bool ReloadCommands(string dllPath, bool showError)
        {
            try
            {
                string fullPath = Path.GetFullPath(dllPath);
                if (!File.Exists(fullPath))
                {
                    AddMissingDllNode(fullPath);
                    return false;
                }

                // 界面扫描阶段从字节加载 DLL，不创建临时副本，也不锁定编译输出文件。
                Assembly assembly = AssemblyUtils.LoadAssemblyForParsing(fullPath);

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 即使部分依赖缺失，也展示其余能够成功读取的命令类型。
                    types = ex.Types.Where(type => type != null).ToArray();
                }

                List<string> commands = new List<string>();
                foreach (Type type in types)
                {
                    foreach (MethodInfo method in type.GetMethods())
                    {
                        object[] attributes = method.GetCustomAttributes(typeof(CommandMethodAttribute), false);
                        foreach (CommandMethodAttribute commandAttribute in attributes.OfType<CommandMethodAttribute>())
                        {
                            if (!string.IsNullOrWhiteSpace(commandAttribute.GlobalName))
                            {
                                commands.Add(commandAttribute.GlobalName);
                            }
                        }
                    }
                }

                UpdateDllNode(fullPath, commands
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(command => command, StringComparer.OrdinalIgnoreCase));
                return true;
            }
            catch (System.Exception ex)
            {
                if (showError)
                {
                    System.Windows.MessageBox.Show(
                        "无法读取 DLL 中的命令：\r\n" + ex.Message,
                        "AutoCADAddInManager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return false;
            }
            finally
            {
                AssemblyUtils.EndAssemblyParsing();
            }
        }

        /// <summary>
        /// 添加或更新一个 DLL 根节点及其命令子节点。
        /// </summary>
        /// <param name="dllPath">DLL 的完整路径。</param>
        /// <param name="commands">DLL 中声明的命令名。</param>
        private void UpdateDllNode(string dllPath, IEnumerable<string> commands)
        {
            TreeViewItem dllNode = FindDllNode(dllPath);
            if (dllNode == null)
            {
                dllNode = new TreeViewItem();
                treeCommands.Items.Add(dllNode);
            }

            dllNode.Header = Path.GetFileName(dllPath);
            dllNode.Tag = dllPath;
            dllNode.ToolTip = dllPath;
            dllNode.FontWeight = FontWeights.SemiBold;
            if (selectedCommandNodes.Any(node => node.Parent == dllNode))
            {
                ClearCommandSelection();
            }

            dllNode.Items.Clear();

            foreach (string command in commands)
            {
                dllNode.Items.Add(new TreeViewItem
                {
                    Header = command,
                    Tag = new CommandEntry(dllPath, command),
                    ToolTip = command,
                    FontWeight = FontWeights.Normal
                });
            }

            dllNode.IsExpanded = true;
            dllNode.IsSelected = true;
        }

        /// <summary>
        /// 添加一个没有命令记录的 DLL 节点。
        /// </summary>
        /// <param name="dllPath">DLL 的历史路径。</param>
        private void AddMissingDllNode(string dllPath)
        {
            if (FindDllNode(dllPath) != null)
            {
                return;
            }

            UpdateDllNode(dllPath, Enumerable.Empty<string>());
        }

        /// <summary>
        /// 按完整路径查找 DLL 根节点。
        /// </summary>
        /// <param name="dllPath">要查找的 DLL 路径。</param>
        /// <returns>匹配的节点；不存在时返回 <see langword="null"/>。</returns>
        private TreeViewItem FindDllNode(string dllPath)
        {
            return treeCommands.Items
                .OfType<TreeViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, dllPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取当前树节点所属 DLL 的完整路径。
        /// </summary>
        /// <returns>当前 DLL 路径；未选中节点时返回 <see langword="null"/>。</returns>
        private string GetSelectedDllPath()
        {
            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            if (selectedNode == null)
            {
                return null;
            }

            CommandEntry command = selectedNode.Tag as CommandEntry;
            return command == null ? selectedNode.Tag as string : command.DllPath;
        }

        /// <summary>
        /// 恢复上一次保存的 DLL 路径及命令树。
        /// </summary>
        private void RestoreDllHistory()
        {
            if (!File.Exists(HistoryFilePath))
            {
                return;
            }

            try
            {
                XDocument document = XDocument.Load(HistoryFilePath);
                foreach (XElement dllElement in document.Descendants("Dll"))
                {
                    string path = (string)dllElement.Attribute("Path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(path);
                    IEnumerable<string> commands = dllElement.Elements("Command")
                            .Select(element => (string)element.Attribute("Name"))
                            .Where(command => !string.IsNullOrWhiteSpace(command))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase);

                    // 恢复阶段只读取持久化元数据，不检查文件，也不把 DLL 加载到当前进程。
                    UpdateDllNode(fullPath, commands);
                }
            }
            catch
            {
                // 历史文件损坏不应影响插件启动，用户下次加载 DLL 时会重新生成。
            }
        }

        /// <summary>
        /// 将当前树中的 DLL 路径和命令名称保存到用户配置目录。
        /// </summary>
        private void SaveDllHistory()
        {
            try
            {
                string directory = Path.GetDirectoryName(HistoryFilePath);
                Directory.CreateDirectory(directory);

                XElement root = new XElement("LoadedDlls",
                    treeCommands.Items
                        .OfType<TreeViewItem>()
                        .Select(CreateDllHistoryElement));

                new XDocument(root).Save(HistoryFilePath);
            }
            catch
            {
                // 配置目录不可写时仍允许本次会话继续使用已加载的 DLL。
            }
        }

        /// <summary>
        /// 创建包含 DLL 路径及当前命令名称的历史配置节点。
        /// </summary>
        /// <param name="dllNode">DLL 树节点。</param>
        /// <returns>可写入历史配置的 XML 节点。</returns>
        private static XElement CreateDllHistoryElement(TreeViewItem dllNode)
        {
            string dllPath = dllNode.Tag as string;
            IEnumerable<XElement> commandElements = dllNode.Items
                .OfType<TreeViewItem>()
                .Select(item => item.Tag as CommandEntry)
                .Where(command => command != null && !string.IsNullOrWhiteSpace(command.CommandName))
                .Select(command => command.CommandName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
                .Select(command => new XElement("Command", new XAttribute("Name", command)));

            return new XElement("Dll", new XAttribute("Path", dllPath), commandElements);
        }

        /// <summary>
        /// 按 Windows 常见选择规则处理命令节点的 Ctrl、Shift 多选。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">鼠标事件参数。</param>
        private void TreeCommands_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TreeViewItem clickedNode = FindTreeViewItem(e.OriginalSource as DependencyObject);
            if (clickedNode == null)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            bool useControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool useShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            string dllPath = clickedNode.Tag as string;
            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                // 双击 DLL 根节点时执行命令运行前的复制和程序集加载，但不调用任何命令。
                if (e.ClickCount == 2 && !useControl && !useShift)
                {
                    LoadPlugin(dllPath);
                    e.Handled = true;
                }

                return;
            }

            CommandEntry clickedCommand = clickedNode.Tag as CommandEntry;
            if (clickedCommand == null)
            {
                return;
            }

            if (useShift && commandSelectionAnchor != null)
            {
                if (!useControl)
                {
                    ClearCommandSelection(false);
                }

                SelectCommandRange(commandSelectionAnchor, clickedNode);
            }
            else if (useControl)
            {
                SetCommandNodeSelected(clickedNode, !selectedCommandNodes.Contains(clickedNode));
                commandSelectionAnchor = clickedNode;
            }
            else
            {
                ClearCommandSelection(false);
                SetCommandNodeSelected(clickedNode, true);
                commandSelectionAnchor = clickedNode;
            }

            SetActiveCommandNode(clickedNode);
            UpdateActionButtonStates();

            // 预览事件会拦截 WPF 原生双击事件，因此在这里直接保留双击运行行为。
            if (e.ClickCount == 2 && !useControl && !useShift)
            {
                RunCommand(clickedCommand);
            }

            e.Handled = true;
        }

        /// <summary>
        /// 复制 DLL 及其依赖并加载程序集，但不执行其中的命令。
        /// </summary>
        /// <param name="dllPath">要加载的插件 DLL 完整路径。</param>
        private void LoadPlugin(string dllPath)
        {
            try
            {
                PluginLoader.LoadPlugin(dllPath);
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "无法加载 DLL：\r\n" + ex.Message,
                    "AutoCADAddInManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 从鼠标命中的可视元素向上查找命令树节点。
        /// </summary>
        /// <param name="source">鼠标命中的元素。</param>
        /// <returns>对应的树节点；未命中节点时返回 <see langword="null"/>。</returns>
        private static TreeViewItem FindTreeViewItem(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null && !(current is TreeViewItem))
            {
                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }

            return current as TreeViewItem;
        }

        /// <summary>
        /// 选择两个命令节点之间所有当前可见的命令。
        /// </summary>
        /// <param name="startNode">范围起点。</param>
        /// <param name="endNode">范围终点。</param>
        private void SelectCommandRange(TreeViewItem startNode, TreeViewItem endNode)
        {
            List<TreeViewItem> visibleCommands = GetVisibleCommandNodes().ToList();
            int startIndex = visibleCommands.IndexOf(startNode);
            int endIndex = visibleCommands.IndexOf(endNode);
            if (startIndex < 0 || endIndex < 0)
            {
                SetCommandNodeSelected(endNode, true);
                commandSelectionAnchor = endNode;
                return;
            }

            int firstIndex = Math.Min(startIndex, endIndex);
            int lastIndex = Math.Max(startIndex, endIndex);
            for (int index = firstIndex; index <= lastIndex; index++)
            {
                SetCommandNodeSelected(visibleCommands[index], true);
            }
        }

        /// <summary>
        /// 按树中的显示顺序枚举所有展开 DLL 下的命令节点。
        /// </summary>
        /// <returns>当前可见的命令节点序列。</returns>
        private IEnumerable<TreeViewItem> GetVisibleCommandNodes()
        {
            foreach (TreeViewItem dllNode in treeCommands.Items.OfType<TreeViewItem>())
            {
                if (!dllNode.IsExpanded)
                {
                    continue;
                }

                foreach (TreeViewItem commandNode in dllNode.Items.OfType<TreeViewItem>())
                {
                    yield return commandNode;
                }
            }
        }

        /// <summary>
        /// 设置命令节点的多选状态及对应视觉效果。
        /// </summary>
        /// <param name="node">要更新的命令节点。</param>
        /// <param name="isSelected">是否选中。</param>
        private void SetCommandNodeSelected(TreeViewItem node, bool isSelected)
        {
            if (isSelected)
            {
                selectedCommandNodes.Add(node);
                node.Background = CommandSelectionBackground;
                node.Foreground = CommandSelectionForeground;
            }
            else
            {
                selectedCommandNodes.Remove(node);
                node.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                node.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            }
        }

        /// <summary>
        /// 清空全部命令多选状态。
        /// </summary>
        /// <param name="clearAnchor">是否同时清除 Shift 选择锚点。</param>
        private void ClearCommandSelection(bool clearAnchor = true)
        {
            foreach (TreeViewItem node in selectedCommandNodes.ToList())
            {
                SetCommandNodeSelected(node, false);
            }

            if (clearAnchor)
            {
                commandSelectionAnchor = null;
            }
        }

        /// <summary>
        /// 设置 WPF TreeView 的单个活动节点，同时保留自定义多选状态。
        /// </summary>
        /// <param name="preferredNode">优先作为活动项的节点。</param>
        private void SetActiveCommandNode(TreeViewItem preferredNode)
        {
            TreeViewItem activeNode = selectedCommandNodes.Contains(preferredNode)
                ? preferredNode
                : selectedCommandNodes.LastOrDefault();

            updatingCommandSelection = true;
            try
            {
                TreeViewItem currentNode = treeCommands.SelectedItem as TreeViewItem;
                if (currentNode != null)
                {
                    currentNode.IsSelected = false;
                }

                if (activeNode != null)
                {
                    activeNode.IsSelected = true;
                }
            }
            finally
            {
                updatingCommandSelection = false;
            }
        }

        /// <summary>
        /// 根据当前 DLL 或命令选择状态更新操作按钮。
        /// </summary>
        private void UpdateActionButtonStates()
        {
            // 清理已从树中移除但仍被 WPF 暂时引用的旧节点。
            foreach (TreeViewItem detachedNode in selectedCommandNodes
                .Where(node => !IsCommandNodeAttached(node))
                .ToList())
            {
                SetCommandNodeSelected(detachedNode, false);
            }

            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            btnRemove.IsEnabled = selectedCommandNodes.Count > 0 || selectedNode != null;

            string selectedDllPath = selectedNode == null ? null : selectedNode.Tag as string;
            btnReloadDll.IsEnabled = !string.IsNullOrEmpty(selectedDllPath);
            btnConfigureDependencies.IsEnabled = !string.IsNullOrEmpty(selectedDllPath)
                && File.Exists(selectedDllPath);

            CommandEntry command = GetSingleSelectedCommand();
            // 文件和命令是否仍然有效，统一延迟到用户实际运行命令时检查。
            btnRun.IsEnabled = command != null;
        }

        /// <summary>
        /// 打开当前 DLL 的附加文件配置窗口，并保存用户选择。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void BtnConfigureDependencies_Click(object sender, EventArgs e)
        {
            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            string dllPath = selectedNode == null ? null : selectedNode.Tag as string;
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
            {
                return;
            }

            try
            {
                DependencyConfigurationWindow dialog = new DependencyConfigurationWindow(
                    dllPath,
                    DependencyConfigurationStore.LoadSelectedFiles(dllPath));
                System.Windows.Window owner = System.Windows.Window.GetWindow(this);
                if (owner != null)
                {
                    dialog.Owner = owner;
                }

                if (dialog.ShowDialog() == true)
                {
                    DependencyConfigurationStore.SaveSelectedFiles(
                        dllPath,
                        dialog.SelectedRelativePaths);
                }
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "无法保存依赖项配置：\r\n" + ex.Message,
                    "AutoCADAddInManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 复制并加载当前选中的 DLL，行为与双击 DLL 根节点一致。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void BtnReloadDll_Click(object sender, EventArgs e)
        {
            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            string dllPath = selectedNode == null ? null : selectedNode.Tag as string;
            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                LoadPlugin(dllPath);
            }
        }

        /// <summary>
        /// 判断一个命令节点是否仍属于当前命令树。
        /// </summary>
        /// <param name="node">要检查的命令节点。</param>
        /// <returns>节点仍在树中时返回 <see langword="true"/>。</returns>
        private bool IsCommandNodeAttached(TreeViewItem node)
        {
            TreeViewItem parentNode = node == null ? null : node.Parent as TreeViewItem;
            return parentNode != null
                && parentNode.Items.Contains(node)
                && treeCommands.Items.Contains(parentNode);
        }

        /// <summary>
        /// 获取当前唯一选中的有效命令，并兼容 WPF 活动节点状态。
        /// </summary>
        /// <returns>唯一选中的命令；没有或多选时返回 <see langword="null"/>。</returns>
        private CommandEntry GetSingleSelectedCommand()
        {
            List<TreeViewItem> attachedNodes = selectedCommandNodes
                .Where(IsCommandNodeAttached)
                .ToList();
            if (attachedNodes.Count == 1)
            {
                return attachedNodes[0].Tag as CommandEntry;
            }

            if (attachedNodes.Count > 1)
            {
                return null;
            }

            TreeViewItem activeNode = treeCommands.SelectedItem as TreeViewItem;
            return IsCommandNodeAttached(activeNode) ? activeNode.Tag as CommandEntry : null;
        }

        /// <summary>
        /// 根据树中选择项控制运行按钮状态。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void TreeCommands_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (updatingCommandSelection)
            {
                return;
            }

            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            CommandEntry command = selectedNode == null ? null : selectedNode.Tag as CommandEntry;
            ClearCommandSelection();
            if (command != null)
            {
                SetCommandNodeSelected(selectedNode, true);
                commandSelectionAnchor = selectedNode;
            }

            UpdateActionButtonStates();
        }

        /// <summary>
        /// 移除当前选中的 DLL 或命令节点。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void BtnRemove_Click(object sender, EventArgs e)
        {
            TreeViewItem selectedNode = treeCommands.SelectedItem as TreeViewItem;
            if (selectedNode == null && selectedCommandNodes.Count == 0)
            {
                return;
            }

            if (selectedCommandNodes.Count > 0)
            {
                // 所有选中的命令节点一起移除，保存时只持久化树中剩余的命令。
                List<TreeViewItem> nodesToRemove = selectedCommandNodes.ToList();
                TreeViewItem nodeToActivate = nodesToRemove
                    .Select(node => node.Parent as TreeViewItem)
                    .FirstOrDefault(node => node != null);

                updatingCommandSelection = true;
                try
                {
                    // 先解除 WPF 活动项，再删除节点，避免 SelectedItem 残留已删除节点。
                    foreach (TreeViewItem commandNode in nodesToRemove)
                    {
                        commandNode.IsSelected = false;
                        TreeViewItem parentNode = commandNode.Parent as TreeViewItem;
                        if (parentNode != null)
                        {
                            parentNode.Items.Remove(commandNode);
                        }
                    }

                    ClearCommandSelection();
                    if (nodeToActivate != null)
                    {
                        nodeToActivate.IsSelected = true;
                    }
                }
                finally
                {
                    updatingCommandSelection = false;
                }

                UpdateActionButtonStates();
                SaveDllHistory();
                return;
            }

            selectedNode.IsSelected = false;
            treeCommands.Items.Remove(selectedNode);
            btnRemove.IsEnabled = false;
            btnRun.IsEnabled = false;
            btnReloadDll.IsEnabled = false;
            btnConfigureDependencies.IsEnabled = false;
            SaveDllHistory();
        }

        /// <summary>
        /// 运行当前选中的命令子节点。
        /// </summary>
        /// <param name="sender">事件发送方。</param>
        /// <param name="e">事件参数。</param>
        private void BtnRun_Click(object sender, EventArgs e)
        {
            RunCommand(GetSingleSelectedCommand());
        }

        /// <summary>
        /// 运行指定命令，并在此时检查 DLL 路径和命令是否仍然有效。
        /// </summary>
        /// <param name="command">要运行的命令。</param>
        private void RunCommand(CommandEntry command)
        {
            if (command == null)
            {
                return;
            }

            try
            {
                PluginLoader.RunCommand(command.DllPath, command.CommandName);
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "无法运行命令：\r\n" + ex.Message,
                    "AutoCADAddInManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 表示命令树中的一个可运行命令。
        /// </summary>
        private sealed class CommandEntry
        {
            /// <summary>
            /// 初始化命令信息。
            /// </summary>
            /// <param name="dllPath">命令所属 DLL 的完整路径。</param>
            /// <param name="commandName">AutoCAD 全局命令名。</param>
            public CommandEntry(string dllPath, string commandName)
            {
                DllPath = dllPath;
                CommandName = commandName;
            }

            /// <summary>
            /// 获取命令所属 DLL 的完整路径。
            /// </summary>
            public string DllPath { get; private set; }

            /// <summary>
            /// 获取 AutoCAD 全局命令名。
            /// </summary>
            public string CommandName { get; private set; }
        }
    }
}
