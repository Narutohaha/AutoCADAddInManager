using Autodesk.AutoCAD.Windows;
using Autodesk.Windows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AutoCADAddInManager
{
    /// <summary>
    /// 管理 AddinManager 可停靠窗口，确保每个 CAD 进程中只创建一个实例。
    /// </summary>
    internal static class AddinManagerPalette
    {
        private static readonly Guid PaletteId = new Guid("A56D5658-8888-42B1-8843-683C30807C28");
        private static PaletteSet paletteSet;

        /// <summary>
        /// 显示 AddinManager 可停靠窗口；首次调用时才创建窗口。
        /// </summary>
        internal static void Show()
        {
            if (paletteSet == null)
            {
                // 处理插件引用的其他 DLL。
                AssemblyUtils.Initialize();

                paletteSet = new PaletteSet("AddinManager", PaletteId)
                {
                    KeepFocus = true,
                    Style = PaletteSetStyles.ShowCloseButton,
                    Size = new Size(300, 0),
                    MinimumSize = new Size(300, 800),
                    DockEnabled = DockSides.Left | DockSides.Right,
                    Dock = DockSides.Left
                };
                paletteSet.AddVisual("6666", new DebuggerControl());
            }

            paletteSet.Visible = true;
        }
    }

    public class AppStart : Autodesk.AutoCAD.Runtime.IExtensionApplication
    {
        private static bool initialized = false;

        public void Initialize()
        {
            CreateTabs();
            ComponentManager.ItemInitialized += ComponentManager_ItemInitialized;
        }

        private void ComponentManager_ItemInitialized(object sender, RibbonItemEventArgs e)
        {
            CreateTabs();
            ComponentManager.ItemInitialized -= ComponentManager_ItemInitialized;
        }

        private void CreateTabs()
        {
            if (Autodesk.AutoCAD.Ribbon.RibbonServices.RibbonPaletteSet == null) return;
            if (initialized) return;
            CreateTab();
            initialized = true;
        }

        private void CreateTab()
        {
            AddinManagerPalette.Show();
        }
       

      
  

        public void Terminate()
        {
            //throw new NotImplementedException();
        }
    }
}

