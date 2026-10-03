using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Depurador;

namespace RevitDwgExploder.Depurador.Tools;

/// <summary>Paso 2: todas las vistas con el plano donde están colocadas ("NA" si no están en ninguno).</summary>
internal sealed class ViewsDepurador : IDepurador
{
	private const string NotPlaced = "NA";

	private sealed class Info
	{
		public bool Placed;

		public bool Is3D;

		public ViewType Type;

		public int Dependents;
	}

	public ToolKind Kind => ToolKind.Views;

	public string Title => "2. Depurador de vistas sin plano";

	public string Guide => "Paso 2 de 4 · Las vistas con plano \"NA\" no están en ningún plano. Las vistas 3D salen con alerta.";

	public string Noun => "vistas";

	public bool CanLocate => true;

	public bool CanSelect => true;

	public bool HasDeepOption => false;

	public ColumnSpec[] Columns { get; } =
	{
		new ColumnSpec("Nombre de vista", 280),
		new ColumnSpec("Tipo", 160),
		new ColumnSpec("Nombre de plano", 280),
		new ColumnSpec("Proceder a eliminar", 125, center: true),
		new ColumnSpec("Alertas", 360, optional: true),
		new ColumnSpec("Nivel", 130, optional: true, hidden: true),
		new ColumnSpec("Plantilla de vista", 190, optional: true, hidden: true),
		new ColumnSpec("Id", 80, optional: true, hidden: true, numeric: true)
	};

	public FilterSpec[] Filters { get; } =
	{
		new FilterSpec("Todas", r => true),
		new FilterSpec("Sin plano (NA)", r => r.Tag is Info i && !i.Placed),
		new FilterSpec("En plano", r => r.Tag is Info i && i.Placed),
		new FilterSpec(Marks.Yes + " Proceder a eliminar", r => r.Verdict == Verdict.Delete),
		new FilterSpec(Marks.Alert + " Revisar antes", r => r.Verdict == Verdict.Review),
		new FilterSpec("Vistas 3D", r => r.Tag is Info i && i.Is3D),
		new FilterSpec("Tablas de planificación", r => r.Tag is Info i && i.Type == ViewType.Schedule),
		new FilterSpec("Leyendas", r => r.Tag is Info i && i.Type == ViewType.Legend),
		new FilterSpec("Con vistas dependientes", r => r.Tag is Info i && i.Dependents > 0)
	};

	public List<DepRow> Collect(UIDocument uiDoc, bool deep, List<string> notes)
	{
		Document doc = uiDoc.Document;
		Placement placement = Placement.Build(doc);
		long activeId = uiDoc.ActiveView?.Id.Value ?? -1;
		HashSet<long> open = RevitUtil.OpenViewIds(uiDoc);
		long startId = -1;
		try
		{
			startId = StartingViewSettings.GetStartingViewSettings(doc).ViewId.Value;
		}
		catch (Exception)
		{
		}

		var rows = new List<DepRow>();
		foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
		{
			if (!IsListed(view))
			{
				continue;
			}

			try
			{
				rows.Add(MakeRow(doc, view, placement, activeId, startId, open));
			}
			catch (Exception ex)
			{
				notes.Add($"No se pudo leer la vista {view.Id.Value}: {ex.Message}");
			}
		}

		return rows
			.OrderBy(r => r.Verdict)
			.ThenBy(r => r.Cells[1], StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(r => r.Cells[0], StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private static bool IsListed(View view)
	{
		if (view.IsTemplate || view is ViewSheet)
		{
			return false;
		}

		switch (view.ViewType)
		{
			case ViewType.ProjectBrowser:
			case ViewType.SystemBrowser:
			case ViewType.Internal:
			case ViewType.Undefined:
			case ViewType.DrawingSheet:
			case ViewType.PanelSchedule:
			case ViewType.Report:
			case ViewType.CostReport:
			case ViewType.LoadsReport:
			case ViewType.PresureLossReport:
			case ViewType.SystemsAnalysisReport:
				return false;
		}

		return !(view is ViewSchedule schedule) || !(schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule);
	}

	private static DepRow MakeRow(Document doc, View view, Placement placement, long activeId, long startId, HashSet<long> open)
	{
		long id = view.Id.Value;
		IReadOnlyList<ViewSheet> sheets = placement.SheetsOf(id);
		bool placed = sheets.Count > 0;
		bool is3D = view is View3D;
		int dependents = view.GetDependentViewIds().Count;
		var alerts = new List<string>();
		bool review = false;
		if (is3D)
		{
			alerts.Add(view.Name.StartsWith("{3D", StringComparison.Ordinal) ? "Vista 3D predeterminada {3D}" : "Vista 3D");
			review = true;
		}

		if (dependents > 0)
		{
			alerts.Add($"Tiene {dependents} vista(s) dependiente(s): se eliminarían con ella");
			review = true;
		}

		if (id == activeId)
		{
			alerts.Add("Es la vista activa: no se puede eliminar");
			review = true;
		}
		else if (open.Contains(id))
		{
			alerts.Add("Abierta en una ventana");
		}

		if (id == startId)
		{
			alerts.Add("Es la vista inicial del proyecto");
			review = true;
		}

		if (view.ViewType == ViewType.Schedule && !placed)
		{
			alerts.Add("Tabla sin plano: revisa si se usa para consultar o exportar");
			review = true;
		}

		ElementId primaryId = view.GetPrimaryViewId();
		if (primaryId != ElementId.InvalidElementId)
		{
			alerts.Add($"Dependiente de '{doc.GetElement(primaryId)?.Name}'");
		}

		Verdict verdict = placed ? Verdict.Keep : review ? Verdict.Review : Verdict.Delete;
		string template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name ?? string.Empty : string.Empty;
		return new DepRow
		{
			Id = id,
			Label = view.Name,
			Verdict = verdict,
			Tag = new Info { Placed = placed, Is3D = is3D, Type = view.ViewType, Dependents = dependents },
			Cells = new[]
			{
				view.Name,
				RevitUtil.ViewTypeLabel(doc, view),
				placed ? string.Join(", ", sheets.Select(RevitUtil.SheetLabel)) : NotPlaced,
				Marks.Of(verdict),
				string.Join("; ", alerts),
				LevelName(view),
				template,
				id.ToString()
			}
		};
	}

	private static string LevelName(View view)
	{
		try
		{
			return view.GenLevel?.Name ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	public IEnumerable<string> DeleteNotes(List<DepRow> rows)
	{
		int placed = rows.Count(r => r.Tag is Info i && i.Placed);
		if (placed > 0)
		{
			yield return $"{placed} están colocadas en planos: se quitarán de esos planos.";
		}

		int dependents = rows.Sum(r => r.Tag is Info i ? i.Dependents : 0);
		if (dependents > 0)
		{
			yield return $"Se eliminarán también {dependents} vista(s) dependiente(s).";
		}

		int threeD = rows.Count(r => r.Tag is Info i && i.Is3D);
		if (threeD > 0)
		{
			yield return $"{threeD} son vistas 3D.";
		}
	}

	public string Delete(UIDocument uiDoc, List<long> ids)
	{
		long activeId = uiDoc.ActiveView?.Id.Value ?? -1;
		return RevitUtil.DeleteElements(uiDoc.Document, ids, "EMASY: eliminar vistas", "vista(s)",
			e => e.Id.Value == activeId ? "es la vista activa (abre otra vista y repite)" : null);
	}

	public string Locate(UIDocument uiDoc, List<long> ids) =>
		ids.Count == 0 ? "Elige una vista." : RevitUtil.Open(uiDoc, uiDoc.Document.GetElement(new ElementId(ids[0])) as View);

	public string Select(UIDocument uiDoc, List<long> ids) => RevitUtil.SelectInRevit(uiDoc, ids);
}
