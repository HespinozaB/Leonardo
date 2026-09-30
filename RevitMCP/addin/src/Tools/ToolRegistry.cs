using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;

namespace RevitMCP.Tools;

/// <summary>Tabla de métodos expuestos al servidor MCP. Los nombres deben coincidir con revit_mcp_server.py.</summary>
internal static class ToolRegistry
{
	public static Dictionary<string, Func<UIApplication, Args, object>> Build()
	{
		return new Dictionary<string, Func<UIApplication, Args, object>>(StringComparer.OrdinalIgnoreCase)
		{
			// Consulta
			["info_proyecto"] = QueryTools.ProjectInfo,
			["listar_niveles"] = QueryTools.Levels,
			["listar_vistas"] = QueryTools.Views,
			["listar_categorias"] = QueryTools.Categories,
			["listar_tipos"] = QueryTools.Types,
			["consultar_elementos"] = QueryTools.Elements,
			["obtener_parametros"] = QueryTools.Parameters,
			["obtener_seleccion"] = QueryTools.Selection,

			// Interfaz
			["seleccionar_elementos"] = UiTools.Select,
			["abrir_vista"] = UiTools.OpenView,
			["captura_vista"] = UiTools.CaptureView,

			// Modificación
			["establecer_parametro"] = ModifyTools.SetParameter,
			["crear_nivel"] = ModifyTools.CreateLevel,
			["crear_muro"] = ModifyTools.CreateWall,
			["crear_suelo"] = ModifyTools.CreateFloor,
			["colocar_familia"] = ModifyTools.PlaceFamily,
			["crear_habitacion"] = ModifyTools.CreateRoom,
			["crear_vista_planta"] = ModifyTools.CreatePlanView,
			["mover_elementos"] = ModifyTools.Move,
			["copiar_elementos"] = ModifyTools.Copy,
			["eliminar_elementos"] = ModifyTools.Delete,

			// Avanzado
			["ejecutar_codigo"] = CodeTool.Execute
		};
	}
}
