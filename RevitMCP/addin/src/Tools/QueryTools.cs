using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;
using static RevitMCP.Tools.RevitHelpers;

namespace RevitMCP.Tools;

internal static class QueryTools
{
	public static object ProjectInfo(UIApplication app, Args a)
	{
		var result = new Dictionary<string, object>
		{
			["revit_version"] = app.Application.VersionNumber,
			["revit_build"] = app.Application.VersionBuild,
			["idioma"] = app.Application.Language.ToString()
		};
		UIDocument uidoc = app.ActiveUIDocument;
		if (uidoc == null)
		{
			result["documento"] = null;
			return result;
		}
		Document doc = uidoc.Document;
		var info = doc.ProjectInformation;
		result["documento"] = new Dictionary<string, object>
		{
			["titulo"] = doc.Title,
			["ruta"] = doc.PathName,
			["es_familia"] = doc.IsFamilyDocument,
			["es_central"] = doc.IsWorkshared,
			["modificado"] = doc.IsModified,
			["nombre_proyecto"] = info?.Name,
			["numero_proyecto"] = info?.Number,
			["cliente"] = info?.ClientName,
			["direccion"] = info?.Address,
			["unidad_longitud"] = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
			["vista_activa"] = uidoc.ActiveView == null ? null : new Dictionary<string, object>
			{
				["id"] = uidoc.ActiveView.Id.Value,
				["nombre"] = uidoc.ActiveView.Name,
				["tipo"] = uidoc.ActiveView.ViewType.ToString()
			},
			["documentos_abiertos"] = app.Application.Documents.Cast<Document>().Select(d => d.Title).ToList()
		};
		return result;
	}

	public static object Levels(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
			.OrderBy(l => l.Elevation)
			.Select(l => new Dictionary<string, object>
			{
				["id"] = l.Id.Value,
				["nombre"] = l.Name,
				["elevacion_mm"] = ToMm(l.Elevation)
			})
			.ToList();
	}

	public static object Views(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		string typeFilter = a.Str("tipo");
		string text = a.Str("texto");
		return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
			.Where(v => !v.IsTemplate)
			.Where(v => typeFilter == null || v.ViewType.ToString().Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
			.Where(v => text == null || v.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
			.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
			.Take(a.Int("limite", 500))
			.Select(v => new Dictionary<string, object>
			{
				["id"] = v.Id.Value,
				["nombre"] = v.Name,
				["tipo"] = v.ViewType.ToString(),
				["nivel"] = v.GenLevel?.Name,
				["numero_hoja"] = (v as ViewSheet)?.SheetNumber
			})
			.ToList();
	}

	public static object Categories(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		bool withCount = a.Bool("con_conteo", true);
		Dictionary<long, int> counts = new Dictionary<long, int>();
		if (withCount)
		{
			foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
			{
				if (e.Category == null)
				{
					continue;
				}
				long key = e.Category.Id.Value;
				counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
			}
		}
		var list = new List<Dictionary<string, object>>();
		foreach (Category cat in doc.Settings.Categories)
		{
			int count = counts.TryGetValue(cat.Id.Value, out int c) ? c : 0;
			if (withCount && count == 0 && !a.Bool("incluir_vacias", false))
			{
				continue;
			}
			var item = new Dictionary<string, object>
			{
				["nombre"] = cat.Name,
				["builtin"] = Enum.IsDefined(typeof(BuiltInCategory), (int)cat.Id.Value) ? ((BuiltInCategory)cat.Id.Value).ToString() : null,
				["tipo"] = cat.CategoryType.ToString()
			};
			if (withCount)
			{
				item["elementos"] = count;
			}
			list.Add(item);
		}
		return list.OrderBy(i => (string)i["nombre"]).ToList();
	}

	public static object Types(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		var collector = new FilteredElementCollector(doc).WhereElementIsElementType();
		if (a.Has("categoria"))
		{
			collector = collector.OfCategory(ResolveCategory(doc, a.Str("categoria")));
		}
		string text = a.Str("texto");
		return collector.Cast<ElementType>()
			.Where(t => text == null || (t.FamilyName + " : " + t.Name).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
			.OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
			.Take(a.Int("limite", 300))
			.Select(t => new Dictionary<string, object>
			{
				["id"] = t.Id.Value,
				["familia"] = t.FamilyName,
				["tipo"] = t.Name,
				["categoria"] = t.Category?.Name,
				["clase"] = t.GetType().Name
			})
			.ToList();
	}

	public static object Elements(UIApplication app, Args a)
	{
		UIDocument uidoc = ActiveUiDoc(app);
		Document doc = uidoc.Document;
		FilteredElementCollector collector = a.Bool("solo_vista_activa", false)
			? new FilteredElementCollector(doc, uidoc.ActiveView.Id)
			: new FilteredElementCollector(doc);
		collector = collector.WhereElementIsNotElementType();
		if (a.Has("categoria"))
		{
			collector = collector.OfCategory(ResolveCategory(doc, a.Str("categoria")));
		}

		IEnumerable<Element> query = collector;
		if (a.Has("nivel"))
		{
			ElementId levelId = ResolveLevel(doc, a.Str("nivel")).Id;
			query = query.Where(e => e.LevelId == levelId);
		}
		string text = a.Str("texto");
		if (text != null)
		{
			query = query.Where(e => (e.Name ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
		}
		if (a.Has("parametro"))
		{
			string paramName = a.Str("parametro");
			string expected = a.Str("valor");
			query = query.Where(e =>
			{
				Parameter p = FindParameter(e, paramName);
				if (p == null)
				{
					return false;
				}
				string actual = p.AsValueString() ?? p.AsString();
				return expected == null || (actual != null && actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
			});
		}

		int limit = a.Int("limite", 100);
		List<Element> all = query.ToList();
		return new Dictionary<string, object>
		{
			["total"] = all.Count,
			["devueltos"] = Math.Min(all.Count, limit),
			["elementos"] = all.Take(limit).Select(e => Describe(doc, e)).ToList()
		};
	}

	public static object Parameters(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		bool includeType = a.Bool("incluir_tipo", false);
		string only = a.Str("filtro");
		var result = new List<Dictionary<string, object>>();
		foreach (long id in a.Ids("ids"))
		{
			Element e = RequireElement(doc, id);
			Dictionary<string, object> info = Describe(doc, e);
			info["parametros"] = ListParameters(e, only);
			if (includeType && doc.GetElement(e.GetTypeId()) is ElementType type)
			{
				info["parametros_tipo"] = ListParameters(type, only);
			}
			result.Add(info);
		}
		return result;
	}

	private static List<Dictionary<string, object>> ListParameters(Element e, string only)
	{
		return e.Parameters.Cast<Parameter>()
			.Where(p => p.Definition != null)
			.Where(p => only == null || p.Definition.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0)
			.OrderBy(p => p.Definition.Name)
			.Select(ParameterInfo)
			.ToList();
	}

	public static object Selection(UIApplication app, Args a)
	{
		UIDocument uidoc = ActiveUiDoc(app);
		Document doc = uidoc.Document;
		return uidoc.Selection.GetElementIds()
			.Select(id => doc.GetElement(id))
			.Where(e => e != null)
			.Take(a.Int("limite", 200))
			.Select(e => Describe(doc, e))
			.ToList();
	}
}
