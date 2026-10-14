using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal static class InfoTools
{
	private const int MaxLimit = 2000;

	public static object GetRevitInfo(UIApplication app, JsonObject args)
	{
		var docs = new List<object>();
		foreach (Document d in app.Application.Documents)
		{
			docs.Add(new { title = d.Title, path = d.PathName, isFamily = d.IsFamilyDocument, isLinked = d.IsLinked, isModified = d.IsModified });
		}

		UIDocument uidoc = app.ActiveUIDocument;
		Document doc = uidoc?.Document;
		return new
		{
			revitVersion = app.Application.VersionNumber,
			revitBuild = app.Application.VersionBuild,
			language = app.Application.Language.ToString(),
			activeDocument = doc == null ? null : new
			{
				title = doc.Title,
				path = doc.PathName,
				isFamily = doc.IsFamilyDocument,
				lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
				activeView = uidoc.ActiveView == null ? null : Describe.Element(uidoc.ActiveView),
				selectionCount = uidoc.Selection.GetElementIds().Count
			},
			openDocuments = docs,
			note = "Coordenadas y longitudes de las herramientas en milímetros; ángulos en grados."
		};
	}

	public static object GetSelection(UIApplication app, JsonObject args)
	{
		UIDocument uidoc = RevitUtil.UiDoc(app);
		bool withParams = Args.Bool(args, "includeParameters", false);
		return uidoc.Selection.GetElementIds()
			.Select(id => uidoc.Document.GetElement(id))
			.Where(e => e != null)
			.Select(e => withParams ? WithParams(e) : Describe.Element(e))
			.ToList();
	}

	public static object ListElements(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		ElementId viewId = Args.Id(args, "viewId");
		if (viewId == null && Args.Bool(args, "activeViewOnly", false))
		{
			viewId = app.ActiveUIDocument.ActiveView.Id;
		}

		FilteredElementCollector collector = viewId != null ? new FilteredElementCollector(doc, viewId) : new FilteredElementCollector(doc);

		if (Args.Str(args, "category") is { Length: > 0 } category)
		{
			collector.OfCategoryId(RevitUtil.FindCategory(doc, category).Id);
		}

		if (Args.Str(args, "className") is { Length: > 0 } className)
		{
			collector.WherePasses(new ElementClassFilter(RevitUtil.FindApiClass(className)));
		}

		if (Args.Bool(args, "typesOnly", false))
		{
			collector.WhereElementIsElementType();
		}
		else
		{
			collector.WhereElementIsNotElementType();
		}

		IEnumerable<Element> elements = collector;
		if (Args.Str(args, "nameContains") is { Length: > 0 } text)
		{
			elements = elements.Where(e => e.Name?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		if (Args.Str(args, "parameterName") is { Length: > 0 } paramName)
		{
			string paramValue = Args.Str(args, "parameterValue");
			elements = elements.Where(e =>
			{
				Parameter p = e.LookupParameter(paramName);
				if (p == null)
				{
					return false;
				}

				return paramValue == null
					|| string.Equals(Describe.Value(p)?.ToString(), paramValue, StringComparison.OrdinalIgnoreCase);
			});
		}

		List<Element> all = elements.ToList();
		int offset = Math.Max(0, Args.Int(args, "offset", 0));
		int limit = Math.Clamp(Args.Int(args, "limit", 100), 1, MaxLimit);
		return new
		{
			total = all.Count,
			offset,
			returned = Math.Max(0, Math.Min(limit, all.Count - offset)),
			elements = all.Skip(offset).Take(limit).Select(Describe.Element).ToList()
		};
	}

	public static object GetElement(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		List<ElementId> ids = Args.RequiredIds(args, "ids");
		bool withType = Args.Bool(args, "includeTypeParameters", false);
		return ids.Select(id =>
		{
			Dictionary<string, object> info = WithParams(RevitUtil.Get(doc, id));
			if (withType && doc.GetElement(RevitUtil.Get(doc, id).GetTypeId()) is ElementType type)
			{
				info["typeParameters"] = Describe.Parameters(type);
			}

			return info;
		}).ToList();
	}

	public static object ListViews(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		string type = Args.Str(args, "viewType");
		bool templates = Args.Bool(args, "includeTemplates", false);

		IEnumerable<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
			.Where(v => templates || !v.IsTemplate)
			.Where(v => v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser);

		if (!string.IsNullOrWhiteSpace(type))
		{
			views = views.Where(v => string.Equals(v.ViewType.ToString(), type, StringComparison.OrdinalIgnoreCase));
		}

		return views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v is ViewSheet s ? s.SheetNumber : v.Name)
			.Select(v =>
			{
				Dictionary<string, object> info = Describe.Element(v);
				if (v is ViewSheet sheet)
				{
					info["placedViews"] = sheet.GetAllPlacedViews().Select(id => doc.GetElement(id)?.Name).ToList();
				}
				else if (v.GenLevel != null)
				{
					info["level"] = v.GenLevel.Name;
				}

				return info;
			}).ToList();
	}

	public static object ListLevels(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
			.OrderBy(l => l.Elevation)
			.Select(l => new { id = l.Id.Value, name = l.Name, elevationMm = Units.ToMm(l.Elevation) })
			.ToList();
	}

	public static object ListFamilyTypes(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		var collector = new FilteredElementCollector(doc).WhereElementIsElementType();
		if (Args.Str(args, "category") is { Length: > 0 } category)
		{
			collector.OfCategoryId(RevitUtil.FindCategory(doc, category).Id);
		}

		IEnumerable<ElementType> types = collector.Cast<ElementType>();
		if (Args.Str(args, "nameContains") is { Length: > 0 } text)
		{
			types = types.Where(t => (t.FamilyName + " : " + t.Name).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		int limit = Math.Clamp(Args.Int(args, "limit", 300), 1, MaxLimit);
		return types.OrderBy(t => t.Category?.Name).ThenBy(t => t.FamilyName).ThenBy(t => t.Name).Take(limit)
			.Select(t => new
			{
				id = t.Id.Value,
				category = t.Category?.Name,
				family = t.FamilyName,
				type = t.Name,
				fullName = t.FamilyName + " : " + t.Name,
				@class = t.GetType().Name,
				isLoadableFamily = t is FamilySymbol
			}).ToList();
	}

	private static Dictionary<string, object> WithParams(Element e)
	{
		Dictionary<string, object> info = Describe.Element(e);
		info["parameters"] = Describe.Parameters(e);
		return info;
	}
}
