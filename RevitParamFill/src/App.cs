using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitParamFill;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

	private static string AddinFolder => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

	/// <summary>ACadSharp pide versiones de System.Memory &amp; co. que pueden no estar cargadas: se resuelven desde la carpeta del addin.</summary>
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

		RibbonPanel panel = application.CreateRibbonPanel(TabName, "Rellenar");
		panel.AddItem(new PushButtonData(
			"FillCommand",
			"Rellenar" + Environment.NewLine + "parámetros",
			Assembly.GetExecutingAssembly().Location,
			"RevitParamFill.Commands.FillCommand")
		{
			ToolTip = "Rellena parámetros (p. ej. los SUNAT) desde la categoría, el tipo, la marca, el material, el nivel u otro parámetro, " +
				"con valores fijos o por sectores dibujados en planta (trazo o DWG). Lo que falta se deduce y se confirma antes de escribir.",
			LargeImage = LoadIcon("RevitParamFill.fill32.b64"),
			Image = LoadIcon("RevitParamFill.fill16.b64"),
			AvailabilityClassName = "RevitParamFill.Commands.FillAvailability"
		});
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
		return Result.Succeeded;
	}

	/// <summary>Carga un PNG incrustado en la DLL como imagen del botón (null si falla: el botón queda sin icono).</summary>
	private static ImageSource LoadIcon(string resourceName)
	{
		try
		{
			// El PNG viaja como texto base64 incrustado en la DLL.
			using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
			if (source == null)
			{
				return null;
			}

			using var reader = new StreamReader(source);
			using var stream = new MemoryStream(Convert.FromBase64String(reader.ReadToEnd().Trim()));

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
