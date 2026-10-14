using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitMcp.Bridge;

namespace RevitMcp;

public class App : IExternalApplication
{
	private const string TabName = "EMASY";

	internal static BridgeServer Server { get; private set; }
	internal static BridgeConfig Config { get; private set; }

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

		RibbonPanel panel = application.CreateRibbonPanel(TabName, "Claude MCP");
		panel.AddItem(new PushButtonData(
			"McpStatusCommand",
			"Estado" + Environment.NewLine + "MCP",
			typeof(App).Assembly.Location,
			"RevitMcp.Commands.StatusCommand")
		{
			ToolTip = "Muestra si el puente con Claude (MCP) está activo, su puerto y cómo configurarlo. " +
				"Permite reiniciarlo.",
			AvailabilityClassName = "RevitMcp.Commands.AlwaysAvailable"
		});

		try
		{
			Config = BridgeConfig.LoadOrCreate();
			StartServer();
		}
		catch (Exception ex)
		{
			TaskDialog.Show("EMASY MCP", "No se pudo iniciar el puente MCP:\n" + ex.Message);
		}

		return Result.Succeeded;
	}

	internal static void StartServer()
	{
		Server?.Dispose();
		Server = new BridgeServer(Config, new RevitDispatcher());
		Server.Start();
	}

	/// <summary>Resuelve las dependencias (Roslyn) desde la carpeta del addin si Revit no las encuentra.</summary>
	private static Assembly ResolveFromAddinFolder(object sender, ResolveEventArgs args)
	{
		string folder = Path.GetDirectoryName(typeof(App).Assembly.Location);
		string file = Path.Combine(folder ?? "", new AssemblyName(args.Name).Name + ".dll");
		return File.Exists(file) ? Assembly.LoadFrom(file) : null;
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		Server?.Dispose();
		return Result.Succeeded;
	}
}
