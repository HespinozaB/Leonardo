using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;

namespace RevitMCP;

[Transaction(TransactionMode.ReadOnly)]
public class ToggleServerCommand : IExternalCommand
{
	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		CommandServer server = App.Server;
		if (server == null)
		{
			message = "El servidor MCP no se inicializó.";
			return Result.Failed;
		}

		if (server.IsRunning)
		{
			server.Stop();
			TaskDialog.Show("RevitMCP", "Servidor MCP detenido.");
		}
		else
		{
			server.Start();
			TaskDialog.Show("RevitMCP", server.IsRunning
				? "Servidor MCP escuchando en 127.0.0.1:" + CommandServer.Port + "."
				: "No se pudo iniciar el servidor MCP:\n" + server.LastError);
		}
		return Result.Succeeded;
	}
}
