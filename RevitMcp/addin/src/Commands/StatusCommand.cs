using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Bridge;
using RevitMcp.Tools;

namespace RevitMcp.Commands;

[Transaction(TransactionMode.ReadOnly)]
public class StatusCommand : IExternalCommand
{
	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		BridgeServer server = App.Server;
		bool running = server?.IsRunning == true;
		var dialog = new TaskDialog("EMASY MCP")
		{
			MainInstruction = running ? "Puente MCP activo" : "Puente MCP detenido",
			MainContent = running
				? $"Escuchando en 127.0.0.1:{App.Config.Port} (solo este equipo).\n" +
					$"Herramientas: {ToolRegistry.Count}. Peticiones atendidas: {server.RequestCount}.\n\n" +
					$"Configuración y token: {BridgeConfig.FilePath}"
				: "No está escuchando." + (server?.LastError is { } error ? "\n\nMotivo: " + error : "") +
					"\n\nSi tienes otra instancia de Revit abierta, ya está usando el puerto. Cambia \"Port\" en " +
					BridgeConfig.FilePath + " (y en el servidor MCP) o cierra la otra instancia.",
			FooterText = "Conecta Claude Desktop / Claude Code con el servidor de la carpeta 'server' (ver README)."
		};
		dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Reiniciar el puente");
		dialog.CommonButtons = TaskDialogCommonButtons.Close;

		if (dialog.Show() == TaskDialogResult.CommandLink1)
		{
			App.StartServer();
			TaskDialog.Show("EMASY MCP", App.Server.IsRunning ? "Puente reiniciado." : "No se pudo reiniciar: " + App.Server.LastError);
		}

		return Result.Succeeded;
	}
}

public class AlwaysAvailable : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
