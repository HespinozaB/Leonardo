using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitDwgExploder;

public class App : IExternalApplication
{
	private const string TabName = "DWG Tools";

	private const string PanelName = "Explotar";

	public Result OnStartup(UIControlledApplication application)
	{
		try
		{
			application.CreateRibbonTab(TabName);
		}
		catch (Autodesk.Revit.Exceptions.ArgumentException)
		{
			// La pestaña ya existe (otro addin la creó).
		}

		RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
		string assemblyPath = Assembly.GetExecutingAssembly().Location;
		var buttonData = new PushButtonData(
			"ExplodeDwgCommand",
			"Explotar" + Environment.NewLine + "DWGs",
			assemblyPath,
			"RevitDwgExploder.Commands.ExplodeDwgCommand")
		{
			ToolTip = "Convierte los DWG importados/vinculados de la vista activa (o los seleccionados) en Detail Lines " +
				"y TextNotes nativos (misma posición, escala y capa → Line Style), sin modificar el DWG original.",
			AvailabilityClassName = "RevitDwgExploder.Commands.ExplodeDwgAvailability"
		};
		panel.AddItem(buttonData);
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		Commands.DwgTextImageOcr.DisposeEngine();
		return Result.Succeeded;
	}
}
