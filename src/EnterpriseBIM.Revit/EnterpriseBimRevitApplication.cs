using System;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using EnterpriseBIM.Revit.Commands;

namespace EnterpriseBIM.Revit
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class EnterpriseBimRevitApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "EnterpriseBIM";
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Ribbon tab already exists in current session.
            }

            RibbonPanel panel = application.CreateRibbonPanel(tabName, "Interoperability Exchange");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData importButtonData = new(
                "EnterpriseBIM_ImportExchange",
                "Import Local\nExchange",
                assemblyPath,
                typeof(EnterpriseBimImportCommand).FullName)
            {
                ToolTip = "Executes resilient transactional reconstruction from %programdata%\\EnterpriseBIM\\exchange.json."
            };

            panel.AddItem(importButtonData);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
