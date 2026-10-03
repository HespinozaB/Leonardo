using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Depurador;

namespace RevitDwgExploder.Depurador.Tools;

/// <summary>Paso 1: los planos del modelo, con sus vistas y tablas, para elegir cuáles conservar y cuáles eliminar.</summary>
internal sealed class SheetsDepurador : IDepurador
{
	private sealed class Info
	{
		public int Views;

		public int Schedules;

		public bool Placeholder;
	}

	public ToolKind Kind => ToolKind.Sheets;

	public string Title => "1. Depurador de planos";

	public string Guide => "Paso 1 de 4 · Marca los planos que no quieras conservar. Sus vistas quedan sin plano y se depuran en el paso 2.";

	public string Noun => "planos";

	public bool CanLocate => true;

	public bool CanSelect => true;

	public bool HasDeepOption => false;

	public ColumnSpec[] Columns { get; } =
	{
		new ColumnSpec("Número", 110),
		new ColumnSpec("Nombre de plano", 280),
		new ColumnSpec("Vistas colocadas", 320),
		new ColumnSpec("Tablas", 240),
		new ColumnSpec("Proceder a eliminar", 125, center: true),
		new ColumnSpec("Alertas", 340, optional: true)
	};

	public FilterSpec[] Filters { get; } =
	{
		new FilterSpec("Todos", r => true),
		new FilterSpec(Marks.Yes + " Proceder a eliminar", r => r.Verdict == Verdict.Delete),
		new FilterSpec(Marks.Alert + " Revisar antes", r => r.Verdict == Verdict.Review),
		new FilterSpec(Marks.No + " En uso (con vistas o tablas)", r => r.Verdict == Verdict.Keep),
		new FilterSpec("Sin vistas ni tablas", r => r.Tag is Info i && i.Views + i.Schedules == 0),
		new FilterSpec("Marcadores de posición", r => r.Tag is Info i && i.Placeholder)
	};

	public List<DepRow> Collect(UIDocument uiDoc, bool deep, List<string> notes)
	{
		Document doc = uiDoc.Document;
		long activeId = uiDoc.ActiveView?.Id.Value ?? -1;
		HashSet<long> open = RevitUtil.OpenViewIds(uiDoc);

		var schedulesBySheet = new Dictionary<long, List<string>>();
		foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
		{
			if (instance.IsTitleblockRevisionSchedule)
			{
				continue;
			}

			if (!schedulesBySheet.TryGetValue(instance.OwnerViewId.Value, out List<string> names))
			{
				names = new List<string>();
				schedulesBySheet[instance.OwnerViewId.Value] = names;
			}

			names.Add(doc.GetElement(instance.ScheduleId)?.Name ?? instance.Name);
		}

		List<ViewSheet> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
		var rows = new List<DepRow>();
		foreach (ViewSheet sheet in sheets)
		{
			try
			{
				rows.Add(MakeRow(doc, sheet, schedulesBySheet, activeId, open));
			}
			catch (Exception ex)
			{
				notes.Add($"No se pudo leer el plano {sheet.Id.Value}: {ex.Message}");
			}
		}

		return rows
			.OrderBy(r => r.Verdict)
			.ThenBy(r => r.Cells[0], StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private static DepRow MakeRow(Document doc, ViewSheet sheet, Dictionary<long, List<string>> schedulesBySheet, long activeId, HashSet<long> open)
	{
		var views = new List<string>();
		foreach (ElementId viewportId in sheet.GetAllViewports())
		{
			if (doc.GetElement(viewportId) is Viewport viewport && doc.GetElement(viewport.ViewId) is View view)
			{
				views.Add(view.Name);
			}
		}

		views.Sort(StringComparer.CurrentCultureIgnoreCase);
		List<string> schedules = schedulesBySheet.TryGetValue(sheet.Id.Value, out List<string> found) ? found : new List<string>();
		schedules.Sort(StringComparer.CurrentCultureIgnoreCase);

		var alerts = new List<string>();
		bool review = false;
		if (sheet.IsPlaceholder)
		{
			alerts.Add("Marcador de posición (solo aparece en listas de planos)");
			review = true;
		}

		if (sheet.Id.Value == activeId)
		{
			alerts.Add("Es la vista activa: no se puede eliminar");
			review = true;
		}
		else if (open.Contains(sheet.Id.Value))
		{
			alerts.Add("Abierto en una ventana");
		}

		if (views.Count > 0)
		{
			alerts.Add($"Si se elimina, sus {views.Count} vista(s) quedan sin plano (paso 2)");
		}

		Verdict verdict = views.Count + schedules.Count > 0 ? Verdict.Keep : review ? Verdict.Review : Verdict.Delete;
		return new DepRow
		{
			Id = sheet.Id.Value,
			Label = RevitUtil.SheetLabel(sheet),
			Verdict = verdict,
			Tag = new Info { Views = views.Count, Schedules = schedules.Count, Placeholder = sheet.IsPlaceholder },
			Cells = new[]
			{
				sheet.SheetNumber,
				sheet.Name,
				Marks.List(views),
				Marks.List(schedules),
				Marks.Of(verdict),
				string.Join("; ", alerts)
			}
		};
	}

	public IEnumerable<string> DeleteNotes(List<DepRow> rows)
	{
		int views = rows.Sum(r => r.Tag is Info i ? i.Views : 0);
		if (views > 0)
		{
			yield return $"Sus {views} vista(s) no se eliminan: quedan sin plano y las puedes depurar en el paso 2.";
		}
	}

	public string Delete(UIDocument uiDoc, List<long> ids)
	{
		long activeId = uiDoc.ActiveView?.Id.Value ?? -1;
		return RevitUtil.DeleteElements(uiDoc.Document, ids, "EMASY: eliminar planos", "plano(s)",
			e => e.Id.Value == activeId ? "es la vista activa (abre otra vista y repite)" : null);
	}

	public string Locate(UIDocument uiDoc, List<long> ids) =>
		ids.Count == 0 ? "Elige un plano." : RevitUtil.Open(uiDoc, uiDoc.Document.GetElement(new ElementId(ids[0])) as View);

	public string Select(UIDocument uiDoc, List<long> ids) => RevitUtil.SelectInRevit(uiDoc, ids);
}
