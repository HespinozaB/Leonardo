using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal delegate object ToolHandler(UIApplication app, JsonObject args);

/// <summary>Herramientas que el servidor MCP puede invocar. El nombre debe coincidir con server/tools.js.</summary>
internal static class ToolRegistry
{
	private static readonly Dictionary<string, ToolHandler> Handlers = new(StringComparer.OrdinalIgnoreCase)
	{
		// Consulta
		["get_revit_info"] = InfoTools.GetRevitInfo,
		["get_selection"] = InfoTools.GetSelection,
		["list_elements"] = InfoTools.ListElements,
		["get_element"] = InfoTools.GetElement,
		["list_views"] = InfoTools.ListViews,
		["list_levels"] = InfoTools.ListLevels,
		["list_family_types"] = InfoTools.ListFamilyTypes,

		// Interfaz
		["set_selection"] = ViewTools.SetSelection,
		["activate_view"] = ViewTools.ActivateView,
		["export_view_image"] = ViewTools.ExportViewImage,

		// Modificación
		["set_parameters"] = EditTools.SetParameters,
		["delete_elements"] = EditTools.DeleteElements,
		["transform_elements"] = EditTools.TransformElements,

		// Creación
		["create_level"] = CreateTools.CreateLevel,
		["create_grid"] = CreateTools.CreateGrid,
		["create_wall"] = CreateTools.CreateWall,
		["create_floor"] = CreateTools.CreateFloor,
		["place_family_instance"] = CreateTools.PlaceFamilyInstance,
		["create_text_note"] = CreateTools.CreateTextNote,
		["create_sheet"] = CreateTools.CreateSheet,
		["place_view_on_sheet"] = CreateTools.PlaceViewOnSheet,

		// Código libre
		["run_csharp"] = ScriptTools.RunCSharp
	};

	public static bool TryGet(string name, out ToolHandler handler) => Handlers.TryGetValue(name, out handler);

	public static int Count => Handlers.Count;
}
