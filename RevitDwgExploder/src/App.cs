using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitDwgExploder;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

	private const string PanelName = "DWG Tools";

	private static readonly string AddinFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

	/// <summary>
	/// Si Revit no encuentra la versión exacta de una DLL de apoyo (System.Memory, System.Buffers…), se usa la que
	/// está en la carpeta del addin: ACadSharp y PdfPig se compilaron contra versiones distintas y los addins
	/// no tienen "binding redirects".
	/// </summary>
	private static Assembly ResolveFromAddinFolder(object sender, ResolveEventArgs args)
	{
		try
		{
			string name = new AssemblyName(args.Name).Name;
			Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
			if (loaded != null)
			{
				return loaded;
			}

			string path = Path.Combine(AddinFolder, name + ".dll");
			return File.Exists(path) ? Assembly.LoadFrom(path) : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	public Result OnStartup(UIControlledApplication application)
	{
		AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddinFolder;

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
			"Explotar en" + Environment.NewLine + "Vista Actual",
			assemblyPath,
			"RevitDwgExploder.Commands.ExplodeDwgCommand")
		{
			ToolTip = "Explota los DWG de la vista actual (o los seleccionados): los convierte en Detail Lines, " +
				"Filled Regions y TextNotes nativos (misma posición, escala, capas, tipos de línea y hatch), sin modificar el DWG original.",
			LargeImage = LoadIcon("RevitDwgExploder.icon32.png"),
			Image = LoadIcon("RevitDwgExploder.icon16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.ExplodeDwgAvailability"
		};
		panel.AddItem(buttonData);

		var finderData = new PushButtonData(
			"DwgFinderCommand",
			"Explotar" + Environment.NewLine + "Varios DWG's",
			assemblyPath,
			"RevitDwgExploder.Commands.DwgFinderCommand")
		{
			ToolTip = "Lista todos los DWG/CAD del modelo y permite explotar varios a la vez (cada uno en su vista), " +
				"además de seleccionarlos, ubicarlos o eliminarlos.",
			LargeImage = LoadIcon("RevitDwgExploder.finder32.png"),
			Image = LoadIcon("RevitDwgExploder.finder16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.DwgFinderAvailability"
		};
		panel.AddItem(finderData);

		RibbonPanel pdfPanel = application.CreateRibbonPanel(TabName, "PDF Tools");
		pdfPanel.AddItem(new PushButtonData(
			"ExplodePdfCommand",
			"Explotar" + Environment.NewLine + "PDF Actual",
			assemblyPath,
			"RevitDwgExploder.Commands.ExplodePdfCommand")
		{
			ToolTip = "Explota los PDF insertados en la vista actual (o los seleccionados) en su lugar: trazos, rellenos y textos " +
				"se convierten en Detail Lines, Filled Regions y TextNotes nativos. Si no hay ninguno, permite elegir un archivo PDF " +
				"e importarlo (una vista de dibujo por página, a la escala del dibujo).",
			LargeImage = LoadIcon("RevitDwgExploder.pdf32.png"),
			Image = LoadIcon("RevitDwgExploder.pdf16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.ExplodePdfAvailability"
		});
		pdfPanel.AddItem(new PushButtonData(
			"PdfFinderCommand",
			"Explotar" + Environment.NewLine + "Varios PDF's",
			assemblyPath,
			"RevitDwgExploder.Commands.PdfFinderCommand")
		{
			ToolTip = "Lista todos los PDF del modelo y permite explotar varios a la vez (cada uno en su vista), importar archivos " +
				"PDF externos, seleccionarlos, ubicarlos o eliminarlos.",
			LargeImage = LoadIcon("RevitDwgExploder.pdffinder32.png"),
			Image = LoadIcon("RevitDwgExploder.pdffinder16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.DwgFinderAvailability"
		});

		RibbonPanel imagePanel = application.CreateRibbonPanel(TabName, "Imágenes Tools");
		imagePanel.AddItem(new PushButtonData(
			"ExplodeImageCommand",
			"Explotar" + Environment.NewLine + "Imagen Actual",
			assemblyPath,
			"RevitDwgExploder.Commands.ExplodeImageCommand")
		{
			ToolTip = "Explota las imágenes (PNG, JPG, BMP, TIF…) insertadas en la vista actual (o las seleccionadas) en su lugar: " +
				"se vectorizan y las manchas de color pasan a Filled Regions, los trazos a Detail Lines y los textos (OCR) a TextNotes. " +
				"Si no hay ninguna, permite elegir un archivo de imagen e importarlo.",
			LargeImage = LoadIcon("RevitDwgExploder.img32.png"),
			Image = LoadIcon("RevitDwgExploder.img16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.ExplodePdfAvailability"
		});
		imagePanel.AddItem(new PushButtonData(
			"ImageFinderCommand",
			"Explotar" + Environment.NewLine + "Varias Imágenes",
			assemblyPath,
			"RevitDwgExploder.Commands.ImageFinderCommand")
		{
			ToolTip = "Lista todas las imágenes del modelo y permite explotar varias a la vez (cada una en su vista), importar " +
				"archivos de imagen externos, seleccionarlas, ubicarlas o eliminarlas.",
			LargeImage = LoadIcon("RevitDwgExploder.imgfinder32.png"),
			Image = LoadIcon("RevitDwgExploder.imgfinder16.png"),
			AvailabilityClassName = "RevitDwgExploder.Commands.DwgFinderAvailability"
		});

		// Depurar Modelo: cuatro pasos en orden (planos, vistas, filtros, parámetros).
		RibbonPanel cleanPanel = application.CreateRibbonPanel(TabName, "Depurar Modelo");
		cleanPanel.AddItem(DepuradorButton(assemblyPath, "SheetsCommand", "1 · Planos" + Environment.NewLine + "sin uso", "sheets",
			"Paso 1. Lista los planos con sus vistas y tablas para elegir cuáles conservar y cuáles eliminar. " +
			"Los planos vacíos salen con ✓; los que tienen vistas o tablas con ✗."));
		cleanPanel.AddItem(DepuradorButton(assemblyPath, "ViewsCommand", "2 · Vistas" + Environment.NewLine + "sin plano", "views",
			"Paso 2. Lista las vistas con el plano donde están colocadas (NA si no están en ninguno). " +
			"Permite eliminarlas, ubicarlas o seleccionarlas. Las vistas 3D salen con alerta."));
		cleanPanel.AddItem(DepuradorButton(assemblyPath, "FiltersCommand", "3 · Filtros" + Environment.NewLine + "sin uso", "filters",
			"Paso 3. Lista los filtros de vista con las vistas, planos y plantillas donde están activos, " +
			"para eliminar los que no se usan."));
		cleanPanel.AddItem(DepuradorButton(assemblyPath, "ParametersCommand", "4 · Parámetros" + Environment.NewLine + "sin uso", "params",
			"Paso 4. Lista los parámetros activos en planos y en tablas, indica si tienen información y permite " +
			"eliminar los residuales. Exporta a CSV."));
		return Result.Succeeded;
	}

	private static PushButtonData DepuradorButton(string assemblyPath, string command, string text, string icon, string tooltip) =>
		new PushButtonData(command, text, assemblyPath, "RevitDwgExploder.Depurador.Commands." + command)
		{
			ToolTip = tooltip,
			LargeImage = LoadIcon($"RevitDwgExploder.{icon}32.png"),
			Image = LoadIcon($"RevitDwgExploder.{icon}16.png"),
			AvailabilityClassName = "RevitDwgExploder.Depurador.Commands.DepuradorAvailability"
		};

	public Result OnShutdown(UIControlledApplication application)
	{
		AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
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
