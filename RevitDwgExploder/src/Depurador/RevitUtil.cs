using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDwgExploder.Depurador;

internal static class RevitUtil
{
	public static string SheetLabel(ViewSheet sheet) =>
		string.IsNullOrWhiteSpace(sheet.SheetNumber) ? sheet.Name : $"{sheet.SheetNumber} - {sheet.Name}";

	public static string SafeName(Element element)
	{
		try
		{
			return element is ViewSheet sheet ? SheetLabel(sheet) : element.Name;
		}
		catch (Exception)
		{
			return element.Id.Value.ToString();
		}
	}

	/// <summary>Nombre del tipo de vista tal como lo muestra Revit (en el idioma de Revit).</summary>
	public static string ViewTypeLabel(Document doc, View view)
	{
		try
		{
			if (doc.GetElement(view.GetTypeId()) is ElementType type && !string.IsNullOrWhiteSpace(type.FamilyName))
			{
				return type.FamilyName;
			}
		}
		catch (Exception)
		{
		}

		return view.ViewType switch
		{
			ViewType.FloorPlan => "Plano de planta",
			ViewType.CeilingPlan => "Plano de techo reflejado",
			ViewType.EngineeringPlan => "Plano de estructura",
			ViewType.AreaPlan => "Plano de área",
			ViewType.Elevation => "Alzado",
			ViewType.Section => "Sección",
			ViewType.Detail => "Vista de detalle",
			ViewType.ThreeD => "Vista 3D",
			ViewType.Schedule => "Tabla de planificación",
			ViewType.ColumnSchedule => "Tabla de pilares",
			ViewType.DraftingView => "Vista de diseño",
			ViewType.Legend => "Leyenda",
			ViewType.Walkthrough => "Recorrido",
			ViewType.Rendering => "Renderización",
			_ => view.ViewType.ToString()
		};
	}

	public static HashSet<long> OpenViewIds(UIDocument uiDoc)
	{
		try
		{
			return new HashSet<long>(uiDoc.GetOpenUIViews().Select(v => v.ViewId.Value));
		}
		catch (Exception)
		{
			return new HashSet<long>();
		}
	}

	/// <summary>Abre la vista o plano en Revit.</summary>
	public static string Open(UIDocument uiDoc, View view)
	{
		if (view == null)
		{
			return "No se encontró la vista.";
		}

		if (view.IsTemplate)
		{
			return $"'{view.Name}' es una plantilla de vista: no se puede abrir.";
		}

		if (view is ViewSheet sheet && sheet.IsPlaceholder)
		{
			return $"'{SheetLabel(sheet)}' es un marcador de posición: no tiene vista que abrir.";
		}

		try
		{
			uiDoc.ActiveView = view;
			return "Abierta: " + SafeName(view);
		}
		catch (Exception ex)
		{
			return $"No se pudo abrir '{SafeName(view)}': {ex.Message}";
		}
	}

	public static string SelectInRevit(UIDocument uiDoc, IEnumerable<long> ids)
	{
		List<ElementId> valid = ids.Select(i => new ElementId(i)).Where(id => uiDoc.Document.GetElement(id) != null).ToList();
		uiDoc.Selection.SetElementIds(valid);
		return $"{valid.Count} elemento(s) seleccionado(s) en Revit.";
	}

	/// <summary>
	/// Elimina los elementos en una sola transacción (se deshace con Ctrl+Z). <paramref name="veto"/> devuelve el motivo
	/// para no eliminar un elemento (o null). Los avisos de Revit se descartan para que no interrumpan.
	/// </summary>
	public static string DeleteElements(Document doc, IList<long> ids, string transactionName, string noun,
		Func<Element, string> veto = null, Func<Document, Element, bool> deleteOne = null)
	{
		int deleted = 0;
		int gone = 0;
		var problems = new List<string>();
		using (var transaction = new Transaction(doc, transactionName))
		{
			transaction.Start();
			FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
			options.SetFailuresPreprocessor(new SwallowWarnings());
			transaction.SetFailureHandlingOptions(options);
			var batch = new List<ElementId>();
			foreach (long id in ids)
			{
				Element element = doc.GetElement(new ElementId(id));
				if (element == null)
				{
					// Ya se eliminó antes (p. ej. una vista dependiente con su vista principal).
					gone++;
					continue;
				}

				string reason = veto?.Invoke(element);
				if (reason != null)
				{
					problems.Add($"{SafeName(element)}: {reason}");
					continue;
				}

				batch.Add(element.Id);
			}

			// Todo de una vez: Revit regenera una sola vez en lugar de una por elemento.
			bool batched = false;
			if (deleteOne == null && batch.Count > 0)
			{
				using (var sub = new SubTransaction(doc))
				{
					sub.Start();
					try
					{
						doc.Delete(batch);
						sub.Commit();
						batched = true;
						deleted = batch.Count(id => doc.GetElement(id) == null);
					}
					catch (Exception)
					{
						// Algún elemento no se puede eliminar: se sigue uno por uno para saber cuál.
						sub.RollBack();
					}
				}
			}

			if (!batched)
			{
				foreach (ElementId id in batch)
				{
					Element element = doc.GetElement(id);
					if (element == null)
					{
						gone++;
						continue;
					}

					string name = SafeName(element);
					try
					{
						bool ok = deleteOne != null ? deleteOne(doc, element) : doc.Delete(id).Count > 0;
						if (ok)
						{
							deleted++;
						}
						else
						{
							problems.Add($"{name}: Revit no lo eliminó");
						}
					}
					catch (Exception ex)
					{
						problems.Add($"{name}: {ex.Message}");
					}
				}
			}

			if (deleted == 0)
			{
				transaction.RollBack();
			}
			else if (transaction.Commit() != TransactionStatus.Committed)
			{
				return "Revit no confirmó la eliminación: no se eliminó nada.";
			}
		}

		string text = $"{deleted} {noun} eliminado(s).";
		if (gone > 0)
		{
			text += $" {gone} ya se habían eliminado (dependientes de otro).";
		}

		if (problems.Count > 0)
		{
			text += $" {problems.Count} no se eliminaron: " + string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? "…" : string.Empty);
		}

		return text;
	}

	private sealed class SwallowWarnings : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
		{
			foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
			{
				if (failure.GetSeverity() == FailureSeverity.Warning)
				{
					accessor.DeleteWarning(failure);
				}
			}

			return FailureProcessingResult.Continue;
		}
	}
}

/// <summary>En qué planos está colocada cada vista (viewports y tablas colocadas).</summary>
internal sealed class Placement
{
	private static readonly IReadOnlyList<ViewSheet> NoSheets = new List<ViewSheet>();

	private readonly Dictionary<long, List<ViewSheet>> _sheets = new Dictionary<long, List<ViewSheet>>();

	public static Placement Build(Document doc)
	{
		var placement = new Placement();
		foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
		{
			placement.Add(viewport.ViewId, doc.GetElement(viewport.SheetId) as ViewSheet);
		}

		foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
		{
			if (!instance.IsTitleblockRevisionSchedule)
			{
				placement.Add(instance.ScheduleId, doc.GetElement(instance.OwnerViewId) as ViewSheet);
			}
		}

		return placement;
	}

	private void Add(ElementId viewId, ViewSheet sheet)
	{
		if (sheet == null || viewId == null || viewId == ElementId.InvalidElementId)
		{
			return;
		}

		if (!_sheets.TryGetValue(viewId.Value, out List<ViewSheet> list))
		{
			list = new List<ViewSheet>();
			_sheets[viewId.Value] = list;
		}

		if (list.All(s => s.Id != sheet.Id))
		{
			list.Add(sheet);
		}
	}

	public bool IsPlaced(long viewId) => _sheets.ContainsKey(viewId);

	public IReadOnlyList<ViewSheet> SheetsOf(long viewId) =>
		_sheets.TryGetValue(viewId, out List<ViewSheet> list) ? list : NoSheets;

	public IEnumerable<long> PlacedViewIds => _sheets.Keys;
}

/// <summary>Dónde se usa un filtro de vista.</summary>
internal sealed class FilterUse
{
	/// <summary>Vistas (no plantillas) donde está aplicado y activado.</summary>
	public readonly List<View> Views = new List<View>();

	/// <summary>Vistas donde está aplicado pero con la casilla "Habilitar filtro" desactivada.</summary>
	public readonly List<View> DisabledIn = new List<View>();

	public readonly List<View> Templates = new List<View>();

	/// <summary>Planos donde aparece alguna de esas vistas.</summary>
	public readonly List<ViewSheet> Sheets = new List<ViewSheet>();

	public bool In3D;
}

internal static class FilterUsage
{
	/// <summary>
	/// Uso de cada filtro de vista (por Id). Si la vista tiene una plantilla que controla los filtros, cuentan los de la
	/// plantilla, que son los que Revit aplica.
	/// </summary>
	public static Dictionary<long, FilterUse> Compute(Document doc, Placement placement)
	{
		var uses = new Dictionary<long, FilterUse>();
		var controls = new Dictionary<long, bool>();
		FilterUse Use(long id)
		{
			if (!uses.TryGetValue(id, out FilterUse use))
			{
				use = new FilterUse();
				uses[id] = use;
			}

			return use;
		}

		foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
		{
			if (view is ViewSheet || view is ViewSchedule)
			{
				continue;
			}

			try
			{
				if (view.IsTemplate)
				{
					foreach (ElementId id in FiltersOf(view))
					{
						Use(id.Value).Templates.Add(view);
					}

					continue;
				}

				View source = view;
				ElementId templateId = view.ViewTemplateId;
				if (templateId != ElementId.InvalidElementId && doc.GetElement(templateId) is View template && ControlsFilters(template, controls))
				{
					source = template;
				}

				foreach (ElementId id in FiltersOf(source))
				{
					FilterUse use = Use(id.Value);
					if (!IsEnabled(source, id))
					{
						use.DisabledIn.Add(view);
						continue;
					}

					use.Views.Add(view);
					if (view is View3D)
					{
						use.In3D = true;
					}

					foreach (ViewSheet sheet in placement.SheetsOf(view.Id.Value))
					{
						if (use.Sheets.All(s => s.Id != sheet.Id))
						{
							use.Sheets.Add(sheet);
						}
					}
				}
			}
			catch (Exception)
			{
				// Una vista ilegible no debe impedir el análisis de las demás.
			}
		}

		return uses;
	}

	private static ICollection<ElementId> FiltersOf(View view)
	{
		try
		{
			return view.AreGraphicsOverridesAllowed() ? view.GetFilters() : new List<ElementId>();
		}
		catch (Exception)
		{
			return new List<ElementId>();
		}
	}

	private static bool IsEnabled(View view, ElementId filterId)
	{
		try
		{
			return view.GetIsFilterEnabled(filterId);
		}
		catch (Exception)
		{
			return true;
		}
	}

	private static bool ControlsFilters(View template, Dictionary<long, bool> cache)
	{
		if (cache.TryGetValue(template.Id.Value, out bool controls))
		{
			return controls;
		}

		try
		{
			long filtersParameter = (long)BuiltInParameter.VIS_GRAPHICS_FILTERS;
			controls = template.GetNonControlledTemplateParameterIds().All(id => id.Value != filtersParameter);
		}
		catch (Exception)
		{
			controls = true;
		}

		cache[template.Id.Value] = controls;
		return controls;
	}
}
