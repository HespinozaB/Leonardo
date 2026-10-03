using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitParamAudit;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

	private const string Availability = "RevitParamAudit.Commands.DepuradorAvailability";

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

		RibbonPanel panel = application.CreateRibbonPanel(TabName, "Depurar Modelo");
		string assembly = Assembly.GetExecutingAssembly().Location;
		panel.AddItem(Button("SheetsCommand", "1 · Planos" + Environment.NewLine + "sin uso", assembly, "sheets",
			"Paso 1. Lista los planos con sus vistas y tablas para elegir cuáles conservar y cuáles eliminar. " +
			"Los planos vacíos salen con ✓; los que tienen vistas o tablas con ✗."));
		panel.AddItem(Button("ViewsCommand", "2 · Vistas" + Environment.NewLine + "sin plano", assembly, "views",
			"Paso 2. Lista las vistas con el plano donde están colocadas (NA si no están en ninguno). " +
			"Permite eliminarlas, ubicarlas o seleccionarlas. Las vistas 3D salen con alerta."));
		panel.AddItem(Button("FiltersCommand", "3 · Filtros" + Environment.NewLine + "sin uso", assembly, "filters",
			"Paso 3. Lista los filtros de vista con las vistas, planos y plantillas donde están activos, " +
			"para eliminar los que no se usan."));
		panel.AddItem(Button("ParametersCommand", "4 · Parámetros" + Environment.NewLine + "sin uso", assembly, "params",
			"Paso 4. Lista los parámetros activos en planos y en tablas, indica si tienen información y permite " +
			"eliminar los residuales. Exporta a CSV."));
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

	private static PushButtonData Button(string command, string text, string assembly, string icon, string tooltip) =>
		new PushButtonData(command, text, assembly, "RevitParamAudit.Commands." + command)
		{
			ToolTip = tooltip,
			LargeImage = LoadIcon($"RevitParamAudit.{icon}32.png"),
			Image = LoadIcon($"RevitParamAudit.{icon}16.png"),
			AvailabilityClassName = Availability
		};

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
