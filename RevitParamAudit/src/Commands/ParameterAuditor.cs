using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitParamAudit.Commands;

/// <summary>Un parámetro del proyecto con el resultado de la auditoría.</summary>
internal sealed class ParamEntry
{
	public const string StatusSheets = "En planos";

	public const string StatusSchedules = "En tablas";

	public const string StatusBoth = "En planos y tablas";

	public const string StatusUnused = "Sin uso";

	public const string StatusGlobal = "Global (no evaluado)";

	public long Id;

	public string Name = string.Empty;

	/// <summary>Proyecto, Compartido o Global.</summary>
	public string Scope = string.Empty;

	public string DataType = string.Empty;

	public string Group = string.Empty;

	/// <summary>Ejemplar, Tipo, "Solo familias" (compartido sin enlace a categorías) o "—" (global).</summary>
	public string Binding = string.Empty;

	public string Categories = string.Empty;

	public string Guid = string.Empty;

	public bool IsGlobal;

	public bool InSheets;

	public bool InSchedules;

	/// <summary>Algún elemento del modelo tiene un valor escrito en este parámetro (se perdería al eliminarlo).</summary>
	public bool HasValues;

	public List<string> SheetReasons = new List<string>();

	public List<string> Schedules = new List<string>();

	public List<string> Warnings = new List<string>();

	public string Status =>
		IsGlobal ? StatusGlobal
		: InSheets && InSchedules ? StatusBoth
		: InSheets ? StatusSheets
		: InSchedules ? StatusSchedules
		: StatusUnused;

	public bool IsUnused => Status == StatusUnused;

	public string SheetsText => string.Join("; ", SheetReasons);

	public string SchedulesText => string.Join("; ", Schedules);

	public string WarningsText => string.Join("; ", Warnings);
}

internal sealed class AuditResult
{
	public List<ParamEntry> Entries = new List<ParamEntry>();

	/// <summary>Avisos de partes del análisis que no se pudieron completar.</summary>
	public List<string> Notes = new List<string>();
}

/// <summary>
/// Revisa todos los parámetros del proyecto (de proyecto, compartidos y globales) y determina dónde se usan:
/// en planos (valores en planos, viewports, vistas colocadas, cajetines, información de proyecto y filtros de las
/// vistas colocadas) y en tablas (campos, incluidos los parámetros combinados, de cualquier tabla de planificación).
/// Lo que no aparece en ninguno de los dos es residual.
/// </summary>
internal static class ParameterAuditor
{
	private static readonly long[] SheetLikeCategories =
	{
		(long)BuiltInCategory.OST_Sheets,
		(long)BuiltInCategory.OST_ProjectInformation,
		(long)BuiltInCategory.OST_TitleBlocks,
		(long)BuiltInCategory.OST_Viewports,
		(long)BuiltInCategory.OST_Views
	};

	public static AuditResult Run(Document doc, bool deep)
	{
		var result = new AuditResult();
		var entries = new Dictionary<long, ParamEntry>();
		var definitions = new Dictionary<long, Definition>();
		var bindings = new Dictionary<long, ElementBinding>();

		ReadBindings(doc, bindings);
		ReadParameters(doc, entries, definitions, bindings);

		HashSet<long> placedViews = new HashSet<long>();
		try
		{
			placedViews = PlacedViewIds(doc);
		}
		catch (Exception ex)
		{
			result.Notes.Add("No se pudieron leer las vistas de los planos: " + ex.Message);
		}

		Step(result, "tablas", () => FindScheduleUses(doc, entries, placedViews));
		Step(result, "valores en planos", () => FindSheetValues(doc, entries, definitions, placedViews));
		Step(result, "filtros de vista", () => FindFilterUses(doc, entries, placedViews));
		if (deep)
		{
			Step(result, "elementos visibles en planos", () => FindVisibleElementValues(doc, entries, definitions, bindings, placedViews));
		}

		Step(result, "valores en el modelo", () => FindValuesAndWarnings(doc, entries, definitions, bindings));

		result.Entries = entries.Values
			.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
		return result;
	}

	private static void Step(AuditResult result, string what, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			result.Notes.Add($"El análisis de {what} no se completó: {ex.Message}");
		}
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Lectura de parámetros

	private static void ReadBindings(Document doc, Dictionary<long, ElementBinding> bindings)
	{
		DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
		it.Reset();
		while (it.MoveNext())
		{
			if (it.Key is InternalDefinition internalDef && it.Current is ElementBinding binding)
			{
				bindings[internalDef.Id.Value] = binding;
			}
		}
	}

	private static void ReadParameters(Document doc, Dictionary<long, ParamEntry> entries,
		Dictionary<long, Definition> definitions, Dictionary<long, ElementBinding> bindings)
	{
		var elements = new Dictionary<long, ParameterElement>();
		foreach (ParameterElement pe in new FilteredElementCollector(doc).OfClass(typeof(ParameterElement)).Cast<ParameterElement>())
		{
			elements[pe.Id.Value] = pe;
		}

		try
		{
			foreach (ParameterElement pe in new FilteredElementCollector(doc).OfClass(typeof(GlobalParameter)).Cast<ParameterElement>())
			{
				elements[pe.Id.Value] = pe;
			}
		}
		catch (Exception)
		{
			// Algunas versiones no permiten filtrar por esta clase: ya vienen en la lista anterior.
		}

		foreach (ParameterElement pe in elements.Values)
		{
			Definition def;
			try
			{
				def = pe.GetDefinition();
			}
			catch (Exception)
			{
				continue;
			}

			if (def == null)
			{
				continue;
			}

			long id = pe.Id.Value;
			bindings.TryGetValue(id, out ElementBinding binding);
			bool isGlobal = pe is GlobalParameter;
			var shared = pe as SharedParameterElement;
			var entry = new ParamEntry
			{
				Id = id,
				Name = def.Name,
				IsGlobal = isGlobal,
				Scope = isGlobal ? "Global" : shared != null ? "Compartido" : "Proyecto",
				DataType = DataTypeLabel(def),
				Group = GroupLabel(def),
				Guid = shared != null ? shared.GuidValue.ToString() : string.Empty,
				Binding = isGlobal ? "—" : binding == null ? "Solo familias" : binding is InstanceBinding ? "Ejemplar" : "Tipo",
				Categories = binding == null ? string.Empty : CategoryNames(binding)
			};
			entries[id] = entry;
			definitions[id] = def;
		}
	}

	private static string DataTypeLabel(Definition def)
	{
		try
		{
			ForgeTypeId type = def.GetDataType();
			if (type == null)
			{
				return string.Empty;
			}

			try
			{
				return LabelUtils.GetLabelForSpec(type);
			}
			catch (Exception)
			{
				return type.TypeId;
			}
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static string GroupLabel(Definition def)
	{
		try
		{
			return LabelUtils.GetLabelForGroup(def.GetGroupTypeId());
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static string CategoryNames(ElementBinding binding)
	{
		var names = new List<string>();
		foreach (Category category in binding.Categories)
		{
			names.Add(category.Name);
		}

		names.Sort(StringComparer.CurrentCultureIgnoreCase);
		return string.Join(", ", names);
	}

	private static bool BoundToSheetLikeCategory(ElementBinding binding)
	{
		foreach (Category category in binding.Categories)
		{
			if (SheetLikeCategories.Contains(category.Id.Value))
			{
				return true;
			}
		}

		return false;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Tablas

	private static void FindScheduleUses(Document doc, Dictionary<long, ParamEntry> entries, HashSet<long> placedViews)
	{
		var onSheets = new HashSet<long>();
		foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
		{
			onSheets.Add(instance.ScheduleId.Value);
		}

		foreach (ViewSchedule schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
		{
			try
			{
				if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
				{
					continue;
				}

				string label = onSheets.Contains(schedule.Id.Value) || placedViews.Contains(schedule.Id.Value)
					? schedule.Name + " (en plano)"
					: schedule.Name;
				var used = new HashSet<long>();
				ScheduleDefinition definition = schedule.Definition;
				for (int i = 0; i < definition.GetFieldCount(); i++)
				{
					ScheduleField field = definition.GetField(i);
					AddId(used, field.ParameterId);
					try
					{
						if (field.IsCombinedParameterField)
						{
							foreach (TableCellCombinedParameterData part in field.GetCombinedParameters())
							{
								AddId(used, part.ParamId);
							}
						}
					}
					catch (Exception)
					{
					}
				}

				foreach (long id in used)
				{
					if (entries.TryGetValue(id, out ParamEntry entry))
					{
						entry.InSchedules = true;
						entry.Schedules.Add(label);
					}
				}
			}
			catch (Exception)
			{
				// Una tabla ilegible no debe impedir el análisis de las demás.
			}
		}
	}

	private static void AddId(HashSet<long> set, ElementId id)
	{
		if (id != null && id != ElementId.InvalidElementId)
		{
			set.Add(id.Value);
		}
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Planos

	private static HashSet<long> PlacedViewIds(Document doc)
	{
		var placed = new HashSet<long>();
		foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
		{
			foreach (ElementId viewId in sheet.GetAllPlacedViews())
			{
				placed.Add(viewId.Value);
			}
		}

		return placed;
	}

	private static void FindSheetValues(Document doc, Dictionary<long, ParamEntry> entries,
		Dictionary<long, Definition> definitions, HashSet<long> placedViews)
	{
		var groups = new List<KeyValuePair<string, List<Element>>>();

		var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<Element>().ToList();
		groups.Add(Pair("Valor en planos", sheets));

		var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Element>().ToList();
		groups.Add(Pair("Valor en viewports", WithTypes(doc, viewports)));

		var views = new List<Element>();
		foreach (long id in placedViews)
		{
			if (doc.GetElement(new ElementId(id)) is View view && !(view is ViewSheet) && !(view is ViewSchedule))
			{
				views.Add(view);
			}
		}

		groups.Add(Pair("Valor en vistas colocadas en planos", WithTypes(doc, views)));

		var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
			.WhereElementIsNotElementType().ToList();
		groups.Add(Pair("Valor en cajetines", WithTypes(doc, titleBlocks)));

		if (doc.ProjectInformation != null)
		{
			groups.Add(Pair("Valor en Información de proyecto (posible etiqueta de cajetín)", new List<Element> { doc.ProjectInformation }));
		}

		foreach (ParamEntry entry in entries.Values)
		{
			if (entry.IsGlobal || !definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			foreach (KeyValuePair<string, List<Element>> group in groups)
			{
				if (group.Value.Any(e => Filled(e, def)))
				{
					entry.InSheets = true;
					entry.SheetReasons.Add(group.Key);
				}
			}
		}
	}

	private static KeyValuePair<string, List<Element>> Pair(string label, List<Element> elements) =>
		new KeyValuePair<string, List<Element>>(label, elements);

	/// <summary>Los elementos más sus tipos (los parámetros de tipo viven en el tipo).</summary>
	private static List<Element> WithTypes(Document doc, List<Element> elements)
	{
		var result = new List<Element>(elements);
		var seen = new HashSet<long>();
		foreach (Element element in elements)
		{
			ElementId typeId = element.GetTypeId();
			if (typeId != null && typeId != ElementId.InvalidElementId && seen.Add(typeId.Value))
			{
				Element type = doc.GetElement(typeId);
				if (type != null)
				{
					result.Add(type);
				}
			}
		}

		return result;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Filtros de vista

	private static void FindFilterUses(Document doc, Dictionary<long, ParamEntry> entries, HashSet<long> placedViews)
	{
		var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();

		// Vistas "efectivas" de los planos: las colocadas y sus plantillas de vista.
		var onSheets = new HashSet<long>(placedViews);
		foreach (View view in allViews)
		{
			if (placedViews.Contains(view.Id.Value) && view.ViewTemplateId != ElementId.InvalidElementId)
			{
				onSheets.Add(view.ViewTemplateId.Value);
			}
		}

		var filterUsed = new HashSet<long>();
		var filterOnSheet = new HashSet<long>();
		foreach (View view in allViews)
		{
			if (view is ViewSheet || view is ViewSchedule)
			{
				continue;
			}

			try
			{
				if (!view.AreGraphicsOverridesAllowed())
				{
					continue;
				}

				bool placed = onSheets.Contains(view.Id.Value);
				foreach (ElementId filterId in view.GetFilters())
				{
					filterUsed.Add(filterId.Value);
					if (placed)
					{
						filterOnSheet.Add(filterId.Value);
					}
				}
			}
			catch (Exception)
			{
			}
		}

		foreach (ParameterFilterElement filter in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
		{
			var parameterIds = new HashSet<long>();
			try
			{
				ElementFilter elementFilter = filter.GetElementFilter();
				if (elementFilter != null)
				{
					CollectRuleParameters(elementFilter, parameterIds);
				}
			}
			catch (Exception)
			{
				continue;
			}

			bool inSheet = filterOnSheet.Contains(filter.Id.Value);
			bool inView = filterUsed.Contains(filter.Id.Value);
			foreach (long parameterId in parameterIds)
			{
				if (!entries.TryGetValue(parameterId, out ParamEntry entry))
				{
					continue;
				}

				if (inSheet)
				{
					entry.InSheets = true;
					entry.SheetReasons.Add($"Filtro de vista '{filter.Name}' (en vista de plano)");
				}
				else
				{
					entry.Warnings.Add($"Usado en el filtro '{filter.Name}'" + (inView ? " (vistas fuera de planos)" : " (sin vistas)"));
				}
			}
		}
	}

	private static void CollectRuleParameters(ElementFilter filter, HashSet<long> ids)
	{
		if (filter is ElementLogicalFilter logical)
		{
			foreach (ElementFilter inner in logical.GetFilters())
			{
				CollectRuleParameters(inner, ids);
			}
		}
		else if (filter is ElementParameterFilter parameterFilter)
		{
			foreach (FilterRule rule in parameterFilter.GetRules())
			{
				ElementId parameterId = rule.GetRuleParameter();
				if (parameterId != null)
				{
					ids.Add(parameterId.Value);
				}
			}
		}
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Análisis profundo: valores en los elementos que se ven en las vistas colocadas en planos

	private static void FindVisibleElementValues(Document doc, Dictionary<long, ParamEntry> entries,
		Dictionary<long, Definition> definitions, Dictionary<long, ElementBinding> bindings, HashSet<long> placedViews)
	{
		var visible = new HashSet<long>();
		foreach (long viewId in placedViews)
		{
			if (!(doc.GetElement(new ElementId(viewId)) is View view) || view is ViewSheet || view is ViewSchedule)
			{
				continue;
			}

			try
			{
				foreach (ElementId id in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds())
				{
					visible.Add(id.Value);
				}
			}
			catch (Exception)
			{
			}
		}

		var byCategory = new Dictionary<long, List<Element>>();
		foreach (long id in visible)
		{
			Element element = doc.GetElement(new ElementId(id));
			long? categoryId = element?.Category?.Id.Value;
			if (categoryId == null)
			{
				continue;
			}

			if (!byCategory.TryGetValue(categoryId.Value, out List<Element> list))
			{
				list = new List<Element>();
				byCategory[categoryId.Value] = list;
			}

			list.Add(element);
		}

		foreach (ParamEntry entry in entries.Values)
		{
			if (entry.IsGlobal || entry.InSheets || !bindings.TryGetValue(entry.Id, out ElementBinding binding) || !definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			bool found = false;
			foreach (Category category in binding.Categories)
			{
				if (byCategory.TryGetValue(category.Id.Value, out List<Element> elements) && elements.Any(e => Filled(e, def) || FilledOnType(doc, e, def)))
				{
					found = true;
					break;
				}
			}

			if (found)
			{
				entry.InSheets = true;
				entry.SheetReasons.Add("Valor en elementos visibles en vistas de planos");
			}
		}
	}

	private static bool FilledOnType(Document doc, Element element, Definition def)
	{
		try
		{
			ElementId typeId = element.GetTypeId();
			return typeId != null && typeId != ElementId.InvalidElementId && Filled(doc.GetElement(typeId), def);
		}
		catch (Exception)
		{
			return false;
		}
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Valores en el modelo y advertencias (solo para los residuales)

	private static void FindValuesAndWarnings(Document doc, Dictionary<long, ParamEntry> entries,
		Dictionary<long, Definition> definitions, Dictionary<long, ElementBinding> bindings)
	{
		foreach (ParamEntry entry in entries.Values)
		{
			if (!entry.IsUnused)
			{
				entry.Warnings.Clear();
				continue;
			}

			if (entry.Binding == "Solo familias")
			{
				entry.Warnings.Add("Sin enlace a categorías: viene de familias cargadas (reaparece al recargarlas)");
			}

			if (!bindings.TryGetValue(entry.Id, out ElementBinding binding) || !definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			if (BoundToSheetLikeCategory(binding))
			{
				entry.Warnings.Add("Enlazado a planos/vistas/cajetín/info. de proyecto: puede ser una etiqueta del cajetín aunque esté vacío");
			}

			entry.HasValues = AnyValue(doc, def, binding);
			if (entry.HasValues)
			{
				entry.Warnings.Add("Tiene valores escritos en elementos: se perderían");
			}
		}
	}

	private static bool AnyValue(Document doc, Definition def, ElementBinding binding)
	{
		try
		{
			var categoryIds = new List<ElementId>();
			foreach (Category category in binding.Categories)
			{
				categoryIds.Add(category.Id);
			}

			if (categoryIds.Count == 0)
			{
				return false;
			}

			using (var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categoryIds)))
			{
				foreach (Element element in collector)
				{
					if (Filled(element, def))
					{
						return true;
					}
				}
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	/// <summary>El parámetro existe en el elemento y tiene un valor distinto del predeterminado (vacío / 0).</summary>
	private static bool Filled(Element element, Definition def)
	{
		try
		{
			Parameter parameter = element?.get_Parameter(def);
			if (parameter == null || !parameter.HasValue)
			{
				return false;
			}

			switch (parameter.StorageType)
			{
				case StorageType.String:
					return !string.IsNullOrWhiteSpace(parameter.AsString());
				case StorageType.Integer:
					return parameter.AsInteger() != 0;
				case StorageType.Double:
					return Math.Abs(parameter.AsDouble()) > 1e-12;
				case StorageType.ElementId:
					return parameter.AsElementId() != ElementId.InvalidElementId;
				default:
					return false;
			}
		}
		catch (Exception)
		{
			return false;
		}
	}
}
