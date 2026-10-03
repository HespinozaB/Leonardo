using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamAudit.Core;

namespace RevitParamAudit.Tools;

/// <summary>
/// Paso 3: los filtros de vista (de reglas y de selección) con las vistas, planos y plantillas donde están aplicados.
/// Las tablas de planificación no usan filtros de vista (sus filtros son internos de cada tabla), así que eliminar un
/// filtro de vista nunca afecta a una tabla.
/// </summary>
internal sealed class FiltersDepurador : IDepurador
{
	private sealed class Info
	{
		public bool InViews;

		public bool InSheets;

		public bool InTemplates;

		public bool In3D;
	}

	public ToolKind Kind => ToolKind.Filters;

	public string Title => "3. Depurador de filtros";

	public string Guide => "Paso 3 de 4 · Filtros de vista según dónde están activos. Las tablas no usan filtros de vista: borrarlos no las afecta.";

	public string Noun => "filtros";

	public bool CanLocate => true;

	public bool CanSelect => true;

	public bool HasDeepOption => false;

	public ColumnSpec[] Columns { get; } =
	{
		new ColumnSpec("Nombre de filtro", 250),
		new ColumnSpec("Activo en vista", 300),
		new ColumnSpec("Activo en plano", 300),
		new ColumnSpec("En plantillas de vista", 240),
		new ColumnSpec("Proceder a eliminar", 125, center: true),
		new ColumnSpec("Alertas", 320, optional: true),
		new ColumnSpec("Tipo de filtro", 100, optional: true, hidden: true),
		new ColumnSpec("Categorías", 260, optional: true, hidden: true),
		new ColumnSpec("Id", 80, optional: true, hidden: true, numeric: true)
	};

	public FilterSpec[] Filters { get; } =
	{
		new FilterSpec("Todos", r => true),
		new FilterSpec(Marks.Yes + " Proceder a eliminar (sin uso)", r => r.Verdict == Verdict.Delete),
		new FilterSpec(Marks.Alert + " Revisar antes", r => r.Verdict == Verdict.Review),
		new FilterSpec(Marks.No + " Activos en planos", r => r.Verdict == Verdict.Keep),
		new FilterSpec("Activos en alguna vista", r => r.Tag is Info i && i.InViews),
		new FilterSpec("En plantillas de vista", r => r.Tag is Info i && i.InTemplates),
		new FilterSpec("Activos en vistas 3D", r => r.Tag is Info i && i.In3D)
	};

	public List<DepRow> Collect(UIDocument uiDoc, bool deep, List<string> notes)
	{
		Document doc = uiDoc.Document;
		Placement placement = Placement.Build(doc);
		Dictionary<long, FilterUse> uses = FilterUsage.Compute(doc, placement);
		var rows = new List<DepRow>();
		foreach (FilterElement filter in AllFilters(doc))
		{
			try
			{
				FilterUse use = uses.TryGetValue(filter.Id.Value, out FilterUse found) ? found : new FilterUse();
				rows.Add(MakeRow(doc, filter, use));
			}
			catch (Exception ex)
			{
				notes.Add($"No se pudo leer el filtro {filter.Id.Value}: {ex.Message}");
			}
		}

		return rows
			.OrderBy(r => r.Verdict)
			.ThenBy(r => r.Cells[0], StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private static IEnumerable<FilterElement> AllFilters(Document doc) =>
		new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<FilterElement>()
			.Concat(new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<FilterElement>());

	private static DepRow MakeRow(Document doc, FilterElement filter, FilterUse use)
	{
		List<string> views = Names(use.Views);
		List<string> sheets = use.Sheets.Select(RevitUtil.SheetLabel).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		List<string> templates = Names(use.Templates);
		var alerts = new List<string>();
		if (use.In3D)
		{
			alerts.Add("Activo en vista 3D");
		}

		if (use.DisabledIn.Count > 0)
		{
			alerts.Add("Aplicado pero desactivado en: " + string.Join(", ", Names(use.DisabledIn)));
		}

		if (templates.Count > 0)
		{
			alerts.Add("Al eliminarlo se quita también de sus plantillas de vista");
		}

		if (views.Count > 0 && sheets.Count == 0)
		{
			alerts.Add("Solo en vistas que no están en planos");
		}

		bool applied = views.Count > 0 || templates.Count > 0 || use.DisabledIn.Count > 0;
		Verdict verdict = sheets.Count > 0 ? Verdict.Keep : applied ? Verdict.Review : Verdict.Delete;
		bool isSelection = filter is SelectionFilterElement;
		return new DepRow
		{
			Id = filter.Id.Value,
			Label = filter.Name,
			Verdict = verdict,
			Tag = new Info { InViews = views.Count > 0, InSheets = sheets.Count > 0, InTemplates = templates.Count > 0, In3D = use.In3D },
			Cells = new[]
			{
				filter.Name,
				Marks.List(views),
				Marks.List(sheets),
				Marks.List(templates),
				Marks.Of(verdict),
				string.Join("; ", alerts),
				isSelection ? "Selección" : "Reglas",
				Categories(doc, filter),
				filter.Id.Value.ToString()
			}
		};
	}

	private static List<string> Names(IEnumerable<View> views) =>
		views.Select(v => v.Name).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

	private static string Categories(Document doc, FilterElement filter)
	{
		try
		{
			if (filter is SelectionFilterElement selection)
			{
				return $"{selection.GetElementIds().Count} elemento(s) elegidos a mano";
			}

			if (filter is ParameterFilterElement parameters)
			{
				return string.Join(", ", parameters.GetCategories()
					.Select(id => Category.GetCategory(doc, id)?.Name)
					.Where(n => !string.IsNullOrEmpty(n))
					.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
			}
		}
		catch (Exception)
		{
		}

		return string.Empty;
	}

	public IEnumerable<string> DeleteNotes(List<DepRow> rows)
	{
		int inViews = rows.Count(r => r.Tag is Info i && (i.InViews || i.InTemplates));
		if (inViews > 0)
		{
			yield return $"{inViews} están aplicados en vistas o plantillas: esas vistas cambiarán de aspecto.";
		}
	}

	public string Delete(UIDocument uiDoc, List<long> ids) =>
		RevitUtil.DeleteElements(uiDoc.Document, ids, "EMASY: eliminar filtros", "filtro(s)");

	/// <summary>Abre la primera vista donde está activo el filtro.</summary>
	public string Locate(UIDocument uiDoc, List<long> ids)
	{
		if (ids.Count == 0)
		{
			return "Elige un filtro.";
		}

		Document doc = uiDoc.Document;
		Dictionary<long, FilterUse> uses = FilterUsage.Compute(doc, Placement.Build(doc));
		if (!uses.TryGetValue(ids[0], out FilterUse use) || use.Views.Count == 0)
		{
			return "Ese filtro no está activo en ninguna vista (como mucho en plantillas): no hay vista que abrir.";
		}

		long activeId = uiDoc.ActiveView?.Id.Value ?? -1;
		View view = use.Views.FirstOrDefault(v => v.Id.Value != activeId) ?? use.Views[0];
		return RevitUtil.Open(uiDoc, view);
	}

	/// <summary>Selecciona en Revit los elementos del modelo que cumplen el filtro (o los elegidos en un filtro de selección).</summary>
	public string Select(UIDocument uiDoc, List<long> ids)
	{
		Document doc = uiDoc.Document;
		var result = new HashSet<ElementId>();
		foreach (long id in ids)
		{
			try
			{
				Element element = doc.GetElement(new ElementId(id));
				if (element is SelectionFilterElement selection)
				{
					result.UnionWith(selection.GetElementIds().Where(e => doc.GetElement(e) != null));
				}
				else if (element is ParameterFilterElement parameters)
				{
					List<ElementId> categories = parameters.GetCategories().ToList();
					if (categories.Count == 0)
					{
						continue;
					}

					var collector = new FilteredElementCollector(doc)
						.WhereElementIsNotElementType()
						.WherePasses(new ElementMulticategoryFilter(categories));
					ElementFilter rules = parameters.GetElementFilter();
					if (rules != null)
					{
						collector = collector.WherePasses(rules);
					}

					result.UnionWith(collector.ToElementIds());
				}
			}
			catch (Exception)
			{
			}
		}

		uiDoc.Selection.SetElementIds(result.ToList());
		return $"{result.Count} elemento(s) del modelo cumplen el filtro y quedaron seleccionados.";
	}
}
