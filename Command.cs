using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AutoCADAddInManager;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;

[assembly: CommandClass(typeof(CadAddinManager.Command))]

namespace CadAddinManager
{
    public class Command
    {
        // 定义一个命令来调出调试器界面
        [CommandMethod("AddinManager")]
        public void ShowAddinManager()
        {
            AddinManagerPalette.Show();
        }

        
    }
}
