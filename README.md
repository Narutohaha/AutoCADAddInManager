# AutoCAD AddIn Manager

[中文](#中文) | [English](#english)

一个用于浏览、加载和运行 AutoCAD .NET 插件命令的开发辅助工具。它会从插件副本加载程序集，避免锁定原始构建产物，从而缩短“修改代码 → 重新编译 → 再次运行”的调试循环。

A development utility for browsing, loading, and running commands from AutoCAD .NET plug-ins. Assemblies are loaded from temporary copies so the original build output remains unlocked, shortening the edit-build-run debugging loop.

---

## 中文

### 功能特性

- 扫描 DLL 中标记了 `CommandMethod` 的 AutoCAD 全局命令，并以树形列表展示。
- 从内存读取 DLL 和同名 PDB，扫描命令时不会锁定原始文件。
- 每次运行前，将插件及其依赖复制到独立临时目录后再加载，方便重新编译和重复调试。
- 自动处理主 DLL 的同名文件，以及运行时引用的托管 DLL/EXE 依赖。
- 支持为每个插件配置需要额外复制的资源或配置文件，并保留相对目录结构。
- 保存 DLL、命令列表和依赖项选择，下次启动 AutoCAD 时恢复界面状态。
- 提供可停靠面板，也可通过 `AddinManager` 命令重新显示。

### 环境要求

- Windows 和 64 位 AutoCAD。
- Visual Studio 2022 或可构建旧式 C# 项目的 MSBuild。
- .NET Framework 4.7 Developer Pack。
- 当前 `cad/` 中引用的 AutoCAD 托管程序集文件版本为 `24.0`。如需面向其他 AutoCAD 版本，请用目标版本 SDK 或安装目录中的对应程序集替换引用，并确认目标 .NET Framework 与该版本兼容。

> 仓库中的 Autodesk DLL 仅作为编译引用，项目已将其 `Copy Local` 设置为 `False`；运行时由 AutoCAD 提供这些程序集。

### 构建

在 Developer PowerShell for Visual Studio 中运行：

```powershell
msbuild .\AutoCADAddInManager.sln /p:Configuration=Release /p:Platform="Any CPU"
```

输出文件位于：

```text
Release\AutoCADAddInManager.dll
```

调试配置则输出到 `Debug\AutoCADAddInManager.dll`。项目目标平台实际为 `x64`。

### 安装与启动

1. 启动 AutoCAD。
2. 在命令行输入 `NETLOAD`。
3. 选择构建生成的 `AutoCADAddInManager.dll`。
4. AddIn Manager 可停靠面板会在初始化后显示；如果面板被关闭，在命令行输入 `AddinManager` 可再次打开。

当前仓库未提供 `.bundle` 安装包或自动启动配置。若要让插件随 AutoCAD 自动加载，请按目标 AutoCAD 版本的部署方式创建 ApplicationPlugins bundle，或使用受信任的其他加载机制。

### 使用方法

1. 点击 **加载 DLL**，选择待调试的 AutoCAD .NET 插件。
2. 展开 DLL 节点，查看扫描出的全局命令。
3. 双击命令，或选中后点击 **运行命令**。
4. 修改并重新编译目标插件后，再次选择该 DLL 以刷新命令列表，然后重新运行。

其他操作：

- **重载 DLL**：复制并加载当前 DLL，但不执行命令；双击 DLL 根节点效果相同。
- **配置依赖项**：选择运行时需要额外复制的文件。主 DLL、同名文件和托管程序集依赖通常无需手动选择。
- **移除**：从面板及持久化历史中删除所选 DLL 或命令，不会删除磁盘上的源文件。
- `Ctrl`/`Shift` + 单击：多选命令节点，便于从列表中批量移除。

### 加载机制

```text
目标插件 DLL
  ├─ 内存读取：发现 CommandMethod，不锁定构建输出
  └─ 运行命令
       ├─ 复制主 DLL 与同名文件
       ├─ 复制用户配置的附加文件
       ├─ 按需解析并复制托管依赖
       └─ 从独立临时目录加载并调用命令方法
```

临时文件写入 `%TEMP%\AutoCADAddInManager\`。工具启动时会尝试清理旧会话文件；仍被其他 AutoCAD 进程占用的文件会被保留。

### 配置文件

用户配置保存在 `%APPDATA%\AutoCADAddInManager\`：

| 文件 | 用途 |
| --- | --- |
| `LoadedDlls.xml` | 保存 DLL 路径以及面板中的命令列表 |
| `DependencyFiles.xml` | 保存每个 DLL 额外选择的依赖/资源文件 |

删除这些文件可重置对应设置。历史记录恢复时只读取已保存的元数据，不会立即加载其中的 DLL。

### 待调试插件的约束

- 命令必须使用 Autodesk `CommandMethodAttribute`，且具有非空的全局命令名。
- 当前运行器按无参数方法调用命令；实例命令所在类型需要可创建的无参数构造函数。
- 运行命令时必须存在活动的 AutoCAD 文档，工具会在调用期间锁定该文档。
- 原生 DLL、数据文件、图片等不会作为托管程序集自动解析，请通过 **配置依赖项** 显式选择。
- .NET Framework 无法从当前 AppDomain 卸载已加载的程序集；工具通过新的临时副本加载新版代码，但彻底释放所有已加载版本仍需退出 AutoCAD。

### 项目结构

| 路径 | 说明 |
| --- | --- |
| `AppStart.cs` | 插件生命周期及可停靠面板创建 |
| `DebuggerControl.xaml(.cs)` | DLL/命令列表与主要交互 |
| `PluginLoader.cs` | 插件加载和命令调用 |
| `AssemblyUtils.cs` | 临时复制、内存扫描和程序集解析 |
| `DependencyConfigurationWindow.xaml(.cs)` | 附加文件选择界面 |
| `DependencyConfigurationStore.cs` | 依赖项配置持久化 |
| `cad/` | AutoCAD 托管 API 编译引用 |

---

## English

### Features

- Scans DLLs for AutoCAD global commands decorated with `CommandMethod` and displays them in a tree.
- Reads DLL and matching PDB files from memory while scanning, leaving the original files unlocked.
- Copies a plug-in and its dependencies to an isolated temporary directory before each execution, allowing repeated rebuilds and test runs.
- Automatically handles files sharing the main DLL's base name and managed DLL/EXE dependencies requested at runtime.
- Lets you select extra resource or configuration files for each plug-in while preserving their relative directory structure.
- Persists DLL history, displayed commands, and per-plug-in dependency selections between AutoCAD sessions.
- Provides a dockable palette that can be reopened with the `AddinManager` command.

### Requirements

- Windows and a 64-bit AutoCAD installation.
- Visual Studio 2022 or an MSBuild installation capable of building legacy C# projects.
- .NET Framework 4.7 Developer Pack.
- The AutoCAD managed references currently stored in `cad/` have file version `24.0`. To target another AutoCAD release, replace them with the matching SDK or installation assemblies and verify that release's required .NET Framework version.

> The Autodesk DLLs in this repository are compile-time references only. `Copy Local` is disabled, so AutoCAD supplies them at runtime.

### Build

Run the following from Developer PowerShell for Visual Studio:

```powershell
msbuild .\AutoCADAddInManager.sln /p:Configuration=Release /p:Platform="Any CPU"
```

The result is written to:

```text
Release\AutoCADAddInManager.dll
```

The Debug configuration is written to `Debug\AutoCADAddInManager.dll`. Although the solution configuration is named `Any CPU`, the project target is `x64`.

### Install and start

1. Start AutoCAD.
2. Enter `NETLOAD` at the command line.
3. Select the generated `AutoCADAddInManager.dll`.
4. The dockable AddIn Manager palette appears during initialization. If it is closed, enter `AddinManager` to show it again.

This repository does not currently include an ApplicationPlugins `.bundle` or an autoload configuration. For automatic startup, package the output according to the deployment guidance for your target AutoCAD version, or use another trusted loading mechanism.

### Usage

1. Click **加载 DLL** (Load DLL) and select the AutoCAD .NET plug-in you want to debug.
2. Expand the DLL node to inspect its global commands.
3. Double-click a command, or select it and click **运行命令** (Run Command).
4. After modifying and rebuilding the target plug-in, select that DLL again to refresh its command list, then run the command again.

Additional actions:

- **重载 DLL** (Reload DLL): copies and loads the selected DLL without invoking a command. Double-clicking a DLL root node does the same thing.
- **配置依赖项** (Configure Dependencies): selects extra files that must be copied for execution. The main DLL, same-base-name files, and managed assembly dependencies normally do not need to be selected.
- **移除** (Remove): removes selected DLLs or commands from the palette and saved history; it does not delete source files from disk.
- `Ctrl`/`Shift` + click: selects multiple command nodes for bulk removal from the list.

### How loading works

```text
Target plug-in DLL
  ├─ Read from memory: discover CommandMethod entries without locking output
  └─ Run a command
       ├─ Copy the main DLL and same-base-name files
       ├─ Copy user-selected extra files
       ├─ Resolve and copy managed dependencies on demand
       └─ Load from an isolated temporary directory and invoke the method
```

Temporary files are stored under `%TEMP%\AutoCADAddInManager\`. The tool attempts to remove files from old sessions at startup; files still used by another AutoCAD process are retained.

### Configuration files

User configuration is stored under `%APPDATA%\AutoCADAddInManager\`:

| File | Purpose |
| --- | --- |
| `LoadedDlls.xml` | Stores DLL paths and the command list shown in the palette |
| `DependencyFiles.xml` | Stores extra dependency/resource selections for each DLL |

Delete either file to reset the corresponding settings. Restoring history only reads saved metadata; it does not immediately load the listed DLLs.

### Target plug-in constraints

- Commands must use Autodesk's `CommandMethodAttribute` and provide a non-empty global command name.
- The current runner invokes commands as parameterless methods. An instance command's declaring type must have a usable parameterless constructor.
- An active AutoCAD document must exist. The tool locks that document while invoking the command.
- Native DLLs, data files, images, and similar resources are not resolved as managed assemblies; select them explicitly through **配置依赖项** (Configure Dependencies).
- .NET Framework cannot unload individual assemblies from the current AppDomain. The tool loads newer builds from new temporary copies, but exiting AutoCAD is still required to release every loaded version completely.

### Project layout

| Path | Description |
| --- | --- |
| `AppStart.cs` | Extension lifecycle and dockable palette creation |
| `DebuggerControl.xaml(.cs)` | DLL/command tree and primary interactions |
| `PluginLoader.cs` | Plug-in loading and command invocation |
| `AssemblyUtils.cs` | Temporary copies, in-memory scanning, and assembly resolution |
| `DependencyConfigurationWindow.xaml(.cs)` | Extra-file selection UI |
| `DependencyConfigurationStore.cs` | Dependency configuration persistence |
| `cad/` | AutoCAD managed API compile-time references |
