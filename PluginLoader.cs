using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AutoCADAddInManager;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CadAddinManager
{
    /// <summary>
    /// 按需加载插件程序集并执行其中的 AutoCAD 命令方法。
    /// </summary>
    public class PluginLoader
    {
        /// <summary>
        /// 复制插件及其依赖到临时目录，并将插件程序集加载到当前进程。
        /// </summary>
        /// <param name="dllPath">插件 DLL 的完整路径。</param>
        /// <returns>已从临时目录加载的插件程序集。</returns>
        public static Assembly LoadPlugin(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                throw new FileNotFoundException("找不到 DLL：" + dllPath, dllPath);
            }

            return AssemblyUtils.LoadAssembly(dllPath);
        }

        /// <summary>
        /// 加载指定 DLL，并执行与命令名称匹配的方法。
        /// </summary>
        /// <param name="dllPath">插件 DLL 的完整路径。</param>
        /// <param name="commandMethodName">要执行的 AutoCAD 全局命令名。</param>
        public static void RunCommand(string dllPath, string commandMethodName)
        {
            if (string.IsNullOrWhiteSpace(commandMethodName))
            {
                throw new ArgumentException("命令名称不能为空。", "commandMethodName");
            }

            Assembly assembly = LoadPlugin(dllPath);
            MethodInfo targetMethod = null;

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods())
                {
                    foreach (CommandMethodAttribute attribute in method
                        .GetCustomAttributes(typeof(CommandMethodAttribute), false)
                        .OfType<CommandMethodAttribute>())
                    {
                        if (string.Equals(
                            attribute.GlobalName,
                            commandMethodName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            targetMethod = method;
                            break;
                        }
                    }

                    if (targetMethod != null)
                    {
                        break;
                    }
                }

                if (targetMethod != null)
                {
                    break;
                }
            }

            if (targetMethod == null)
            {
                throw new MissingMethodException(
                    "在 DLL 中找不到命令“" + commandMethodName + "”：" + dllPath);
            }

            object instance = targetMethod.IsStatic
                ? null
                : Activator.CreateInstance(targetMethod.DeclaringType);

            // 在当前文档上下文中执行，确保插件访问数据库时持有文档锁。
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                throw new InvalidOperationException("当前没有可用的 AutoCAD 文档。");
            }

            using (document.LockDocument())
            {
                targetMethod.Invoke(instance, null);
            }
        }
    }
}
