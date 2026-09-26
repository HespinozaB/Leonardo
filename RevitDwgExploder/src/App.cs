using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitDwgExploder;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

	private const string PanelName = "DWG Tools";

	public Result OnStartup(UIControlledApplication application)
	{
		try
		{
			application.CreateRibbonTab(TabName);
		}
		catch (Autodesk.Revit.Exceptions.ArgumentException)
		{
			// La pestaña ya existe (otro addin de EMASY la creó).
		}

		RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
		string assemblyPath = Assembly.GetExecutingAssembly().Location;
		var buttonData = new PushButtonData(
			"ExplodeDwgCommand",
			"Explotar" + Environment.NewLine + "DWGs",
			assemblyPath,
			"RevitDwgExploder.Commands.ExplodeDwgCommand")
		{
			ToolTip = "Convierte los DWG importados/vinculados de la vista activa (o los seleccionados) en Detail Lines, " +
				"Filled Regions y TextNotes nativos (misma posición, escala, capas, tipos de línea y hatch), sin modificar el DWG original.",
			LargeImage = LoadIcon("RevitDwgExploder.icon32.png"),
			Image = LoadIcon("RevitDwgExploder.icon16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.ExplodeDwgAvailability"
		};
		panel.AddItem(buttonData);

		var finderData = new PushButtonData(
			"DwgFinderCommand",
			"Buscador" + Environment.NewLine + "DWG's",
			assemblyPath,
			"RevitDwgExploder.Commands.DwgFinderCommand")
		{
			ToolTip = "Lista todos los archivos DWG/CAD del modelo (vinculados, importados y sin instancias) y permite " +
				"seleccionarlos, ubicarlos o eliminarlos.",
			LargeImage = LoadIcon("RevitDwgExploder.finder32.png"),
			Image = LoadIcon("RevitDwgExploder.finder16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.DwgFinderAvailability"
		};
		panel.AddItem(finderData);
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		Commands.DwgTextImageOcr.DisposeEngine();
		return Result.Succeeded;
	}

	/// <summary>Carga un PNG incrustado en la DLL como imagen del botón (null si falla: el botón queda sin icono).</summary>
	private static ImageSource LoadIcon(string resourceName)
	{
		try
		{
			using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
			if (stream == null)
			{
				return null;
			}

			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.StreamSource = stream;
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
