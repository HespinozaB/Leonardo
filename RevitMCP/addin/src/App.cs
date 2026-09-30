using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;

namespace RevitMCP;

public class App : IExternalApplication
{
	private const string TabName = "MCP";

	private const string PanelName = "Claude";

	internal static CommandServer Server { get; private set; }

	public Result OnStartup(UIControlledApplication application)
	{
		try
		{
			application.CreateRibbonTab(TabName);
		}
		catch (Autodesk.Revit.Exceptions.ArgumentException)
		{
			// La pestaña ya existe.
		}

		RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
		string assemblyPath = Assembly.GetExecutingAssembly().Location;
		panel.AddItem(new PushButtonData(
			"RevitMcpToggle",
			"Servidor" + Environment.NewLine + "MCP",
			assemblyPath,
			"RevitMCP.ToggleServerCommand")
		{
			ToolTip = "Inicia o detiene el puente local que permite a Claude (MCP) operar sobre este Revit. " +
				"Escucha solo en 127.0.0.1, puerto " + CommandServer.Port + "."
		});

		var handler = new RequestHandler();
		Server = new CommandServer(handler, ExternalEvent.Create(handler));
		if (!string.Equals(Environment.GetEnvironmentVariable("REVIT_MCP_AUTOSTART"), "0", StringComparison.Ordinal))
		{
			Server.Start();
		}
		return Result.Succeeded;
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		Server?.Stop();
		return Result.Succeeded;
	}
}
