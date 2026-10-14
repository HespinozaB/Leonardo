using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcp.Tools;

/// <summary>Resúmenes serializables de elementos y parámetros.</summary>
internal static class Describe
{
	public static Dictionary<string, object> Element(Element e)
	{
		var info = new Dictionary<string, object>
		{
			["id"] = e.Id.Value,
			["name"] = e.Name,
			["category"] = e.Category?.Name,
			["class"] = e.GetType().Name
		};

		if (e is FamilyInstance fi)
		{
			info["family"] = fi.Symbol?.FamilyName;
			info["type"] = fi.Symbol?.Name;
		}
		else if (e.GetTypeId() is { } typeId && typeId != ElementId.InvalidElementId)
		{
			info["type"] = e.Document.GetElement(typeId)?.Name;
		}

		if (e.LevelId != ElementId.InvalidElementId && e.Document.GetElement(e.LevelId) is Level level)
		{
			info["level"] = level.Name;
		}

		switch (e.Location)
		{
			case LocationPoint lp:
				info["location"] = Units.Mm(lp.Point);
				break;
			case LocationCurve lc:
				info["start"] = Units.Mm(lc.Curve.GetEndPoint(0));
				info["end"] = Units.Mm(lc.Curve.GetEndPoint(1));
				info["lengthMm"] = Units.ToMm(lc.Curve.Length);
				break;
		}

		if (e is View view)
		{
			info["viewType"] = view.ViewType.ToString();
			info["isTemplate"] = view.IsTemplate;
		}

		if (e is ViewSheet sheet)
		{
			info["sheetNumber"] = sheet.SheetNumber;
		}

		if (e is Level lvl)
		{
			info["elevationMm"] = Units.ToMm(lvl.Elevation);
		}

		return info;
	}

	public static List<Dictionary<string, object>> Parameters(Element e)
	{
		var list = new List<Dictionary<string, object>>();
		foreach (Parameter p in e.Parameters)
		{
			if (p?.Definition == null)
			{
				continue;
			}

			list.Add(new Dictionary<string, object>
			{
				["name"] = p.Definition.Name,
				["value"] = Value(p),
				["storage"] = p.StorageType.ToString(),
				["readOnly"] = p.IsReadOnly,
				["shared"] = p.IsShared
			});
		}

		return list.OrderBy(d => (string)d["name"]).ToList();
	}

	public static object Value(Parameter p)
	{
		if (!p.HasValue)
		{
			return null;
		}

		return p.StorageType switch
		{
			StorageType.String => p.AsString(),
			StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(),
			StorageType.Double => p.AsValueString(),
			StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
			_ => null
		};
	}
}
