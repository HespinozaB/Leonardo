using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitPartidas;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

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

		RibbonPanel panel = application.CreateRibbonPanel(TabName, "Partidas");
		panel.AddItem(new PushButtonData(
			"PartidasCommand",
			"Partidas" + Environment.NewLine + "DYCUSA",
			Assembly.GetExecutingAssembly().Location,
			"RevitPartidas.Commands.PartidasCommand")
		{
			ToolTip = "Llena los parámetros de nivel (DYCUSA_PARTIDA 1 a 6) desde el Código de montaje, con la etapa como filtro. " +
				"Genera los códigos de montaje del presupuesto, sugiere partidas por parámetros o por nombre y muestra la cobertura.",
			LargeImage = LoadIcon("RevitPartidas.partidas32.b64"),
			Image = LoadIcon("RevitPartidas.partidas16.b64"),
			AvailabilityClassName = "RevitPartidas.Commands.PartidasAvailability"
		});
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

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
