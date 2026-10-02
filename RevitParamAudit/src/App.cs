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

		RibbonPanel panel = application.CreateRibbonPanel(TabName, "Parámetros");
		panel.AddItem(new PushButtonData(
			"ParamAuditCommand",
			"Auditar" + Environment.NewLine + "Parámetros",
			Assembly.GetExecutingAssembly().Location,
			"RevitParamAudit.Commands.ParamAuditCommand")
		{
			ToolTip = "Revisa los parámetros del proyecto y los separa en: usados en planos, usados en tablas y " +
				"residuales (sin uso en planos ni tablas). Permite exportar a CSV y eliminar los residuales.",
			LargeImage = LoadIcon("RevitParamAudit.audit32.png"),
			Image = LoadIcon("RevitParamAudit.audit16.png"),
			AvailabilityClassName = "RevitParamAudit.Commands.ParamAuditAvailability"
		});
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

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
