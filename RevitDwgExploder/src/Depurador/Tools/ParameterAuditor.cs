using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgExploder.Depurador;

namespace RevitDwgExploder.Depurador.Tools;

/// <summary>Un parámetro del proyecto con el resultado de la auditoría.</summary>
internal sealed class ParamEntry
{
	public const string FamiliesOnly = "Solo familias";

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

	/// <summary>Usado en un filtro de una vista 3D o con valor en una vista 3D.</summary>
	public bool In3D;

	/// <summary>Algún elemento del modelo tiene información escrita en este parámetro (se perdería al eliminarlo).</summary>
	public bool HasValues;

	public List<string> SheetReasons = new List<string>();

	public List<string> Schedules = new List<string>();

	public List<string> Warnings = new List<string>();

	public bool IsUnused => !IsGlobal && !InSheets && !InSchedules;

	/// <summary>✗ en uso; ⚠ sin uso pero con información, vista 3D o advertencias; ✓ sin uso y vacío; — global.</summary>
	public Verdict Verdict =>
		IsGlobal ? Verdict.NotApplicable
		: InSheets || InSchedules ? Verdict.Keep
		: HasValues || In3D || Warnings.Count > 0 ? Verdict.Review
		: Verdict.Delete;

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
/// vistas colocadas) y en tablas (campos, incluidos los combinados, de cualquier tabla de planificación). También
/// indica si tienen información escrita en algún elemento y si aparecen en vistas 3D.
/// </summary>
internal static class ParameterAuditor
{
	private static readonly HashSet<long> SheetLikeCategories = new HashSet<long>
	{
		(long)BuiltInCategory.OST_Sheets,
		(long)BuiltInCategory.OST_ProjectInformation,
		(long)BuiltInCategory.OST_TitleBlocks,
		(long)BuiltInCategory.OST_Viewports,
		(long)BuiltInCategory.OST_Views
	};

	/// <summary>Un grupo de elementos de los planos donde buscar valores (planos, viewports, cajetines…).</summary>
	private sealed class SheetGroup
	{
		public string Reason;

		public List<Element> Elements;

		public HashSet<long> Categories;

		/// <summary>Elementos de familia: aquí pueden vivir los compartidos que solo están en familias.</summary>
		public bool HoldsFamilyParameters;
	}

	private sealed class Context
	{
		public Document Doc;

		public Dictionary<long, ParamEntry> Entries = new Dictionary<long, ParamEntry>();

		public Dictionary<long, Definition> Definitions = new Dictionary<long, Definition>();

		public Dictionary<long, ElementBinding> Bindings = new Dictionary<long, ElementBinding>();

		public Dictionary<long, HashSet<long>> BoundCategories = new Dictionary<long, HashSet<long>>();

		public Placement Placement;
	}

	public static AuditResult Run(Document doc, bool deep)
	{
		var result = new AuditResult();
		var ctx = new Context { Doc = doc };
		ReadBindings(ctx);
		ReadParameters(ctx);

		try
		{
			ctx.Placement = Placement.Build(doc);
		}
		catch (Exception ex)
		{
			ctx.Placement = new Placement();
			result.Notes.Add("No se pudieron leer las vistas de los planos: " + ex.Message);
		}

		Step(result, "tablas", () => FindScheduleUses(ctx));
		Step(result, "valores en planos", () => FindSheetValues(ctx));
		Step(result, "filtros de vista", () => FindFilterUses(ctx));
		if (deep)
		{
			Step(result, "elementos visibles en planos", () => FindVisibleElementValues(ctx));
		}

		Step(result, "información en el modelo", () => FindValues(ctx));
		Step(result, "advertencias", () => FindWarnings(ctx));

		result.Entries = ctx.Entries.Values
			.OrderBy(e => e.Verdict)
			.ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
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

	private static void ReadBindings(Context ctx)
	{
		DefinitionBindingMapIterator it = ctx.Doc.ParameterBindings.ForwardIterator();
		it.Reset();
		while (it.MoveNext())
		{
			if (it.Key is InternalDefinition internalDef && it.Current is ElementBinding binding)
			{
				long id = internalDef.Id.Value;
				ctx.Bindings[id] = binding;
				var categories = new HashSet<long>();
				foreach (Category category in binding.Categories)
				{
					categories.Add(category.Id.Value);
				}

				ctx.BoundCategories[id] = categories;
			}
		}
	}

	private static void ReadParameters(Context ctx)
	{
		var elements = new Dictionary<long, ParameterElement>();
		foreach (ParameterElement pe in new FilteredElementCollector(ctx.Doc).OfClass(typeof(ParameterElement)).Cast<ParameterElement>())
		{
			elements[pe.Id.Value] = pe;
		}

		try
		{
			foreach (ParameterElement pe in new FilteredElementCollector(ctx.Doc).OfClass(typeof(GlobalParameter)).Cast<ParameterElement>())
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
			ctx.Bindings.TryGetValue(id, out ElementBinding binding);
			bool isGlobal = pe is GlobalParameter;
			var shared = pe as SharedParameterElement;
			ctx.Entries[id] = new ParamEntry
			{
				Id = id,
				Name = def.Name,
				IsGlobal = isGlobal,
				Scope = isGlobal ? "Global" : shared != null ? "Compartido" : "Proyecto",
				DataType = DataTypeLabel(def),
				Group = GroupLabel(def),
				Guid = shared != null ? shared.GuidValue.ToString() : string.Empty,
				Binding = isGlobal ? Marks.None : binding == null ? ParamEntry.FamiliesOnly : binding is InstanceBinding ? "Ejemplar" : "Tipo",
				Categories = binding == null ? string.Empty : CategoryNames(binding)
			};
			ctx.Definitions[id] = def;
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

	// ----------------------------------------------------------------------------------------------------------------
	// Tablas

	private static void FindScheduleUses(Context ctx)
	{
		foreach (ViewSchedule schedule in new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
		{
			try
			{
				if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
				{
					continue;
				}

				string label = ctx.Placement.IsPlaced(schedule.Id.Value) ? schedule.Name + " (en plano)" : schedule.Name;
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
					if (ctx.Entries.TryGetValue(id, out ParamEntry entry))
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
	// Planos (solo se revisan los grupos cuyas categorías están enlazadas al parámetro: es lo que lo hace rápido)

	private static void FindSheetValues(Context ctx)
	{
		Document doc = ctx.Doc;
		var groups = new List<SheetGroup>
		{
			Group("Valor en planos", new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).ToList()),
			Group("Valor en viewports", WithTypes(doc, new FilteredElementCollector(doc).OfClass(typeof(Viewport)).ToList()))
		};

		var placedViews = new List<Element>();
		foreach (long id in ctx.Placement.PlacedViewIds)
		{
			if (doc.GetElement(new ElementId(id)) is View view && !(view is ViewSheet) && !(view is ViewSchedule))
			{
				placedViews.Add(view);
			}
		}

		groups.Add(Group("Valor en vistas colocadas en planos", WithTypes(doc, placedViews)));
		SheetGroup titleBlocks = Group("Valor en cajetines", WithTypes(doc, new FilteredElementCollector(doc)
			.OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList()));
		titleBlocks.HoldsFamilyParameters = true;
		groups.Add(titleBlocks);
		if (doc.ProjectInformation != null)
		{
			groups.Add(Group("Valor en Información de proyecto (posible etiqueta de cajetín)", new List<Element> { doc.ProjectInformation }));
		}

		SheetGroup views3D = Group("Valor en vista 3D", new FilteredElementCollector(doc).OfClass(typeof(View3D))
			.Cast<View3D>().Where(v => !v.IsTemplate).Cast<Element>().ToList());

		foreach (ParamEntry entry in ctx.Entries.Values)
		{
			if (entry.IsGlobal || !ctx.Definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			ctx.BoundCategories.TryGetValue(entry.Id, out HashSet<long> bound);
			foreach (SheetGroup group in groups)
			{
				if (Applies(group, bound) && group.Elements.Any(e => Filled(e, def)))
				{
					entry.InSheets = true;
					entry.SheetReasons.Add(group.Reason);
				}
			}

			if (!entry.InSheets && Applies(views3D, bound) && views3D.Elements.Any(e => Filled(e, def)))
			{
				entry.In3D = true;
				entry.Warnings.Add("Tiene valor en una vista 3D");
			}
		}
	}

	/// <summary>¿Puede el parámetro existir en los elementos del grupo?</summary>
	private static bool Applies(SheetGroup group, HashSet<long> bound) =>
		bound == null ? group.HoldsFamilyParameters : group.Categories.Overlaps(bound);

	private static SheetGroup Group(string reason, List<Element> elements)
	{
		var categories = new HashSet<long>();
		foreach (Element element in elements)
		{
			Category category = element.Category;
			if (category != null)
			{
				categories.Add(category.Id.Value);
			}
		}

		return new SheetGroup { Reason = reason, Elements = elements, Categories = categories };
	}

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

	private static void FindFilterUses(Context ctx)
	{
		Dictionary<long, FilterUse> uses = FilterUsage.Compute(ctx.Doc, ctx.Placement);
		foreach (ParameterFilterElement filter in new FilteredElementCollector(ctx.Doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
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

			uses.TryGetValue(filter.Id.Value, out FilterUse use);
			bool onSheet = use != null && use.Sheets.Count > 0;
			bool inViews = use != null && (use.Views.Count > 0 || use.Templates.Count > 0);
			bool in3D = use != null && use.In3D;
			foreach (long parameterId in parameterIds)
			{
				if (!ctx.Entries.TryGetValue(parameterId, out ParamEntry entry))
				{
					continue;
				}

				if (onSheet)
				{
					entry.InSheets = true;
					entry.SheetReasons.Add($"Filtro de vista '{filter.Name}' (en vista de plano)");
				}
				else
				{
					entry.Warnings.Add($"Usado en el filtro '{filter.Name}'" + (inViews ? " (vistas fuera de planos)" : " (sin vistas)"));
				}

				if (in3D)
				{
					entry.In3D = true;
					entry.Warnings.Add($"Filtro '{filter.Name}' activo en vista 3D");
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

	private static void FindVisibleElementValues(Context ctx)
	{
		Document doc = ctx.Doc;
		var visible = new HashSet<long>();
		foreach (long viewId in ctx.Placement.PlacedViewIds)
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

		foreach (ParamEntry entry in ctx.Entries.Values)
		{
			if (entry.IsGlobal || entry.InSheets
				|| !ctx.BoundCategories.TryGetValue(entry.Id, out HashSet<long> bound)
				|| !ctx.Definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			foreach (long categoryId in bound)
			{
				if (byCategory.TryGetValue(categoryId, out List<Element> elements) && elements.Any(e => Filled(e, def) || FilledOnType(doc, e, def)))
				{
					entry.InSheets = true;
					entry.SheetReasons.Add("Valor en elementos visibles en vistas de planos");
					break;
				}
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
	// Información escrita en elementos (para todos los parámetros)

	/// <summary>
	/// Usa el filtro nativo de Revit "tiene valor" (rápido) sobre las categorías enlazadas; los compartidos que solo
	/// están en familias se buscan en los ejemplares y tipos de familia.
	/// </summary>
	private static void FindValues(Context ctx)
	{
		ElementFilter familyElements = new LogicalOrFilter(new ElementClassFilter(typeof(FamilyInstance)), new ElementClassFilter(typeof(FamilySymbol)));
		foreach (ParamEntry entry in ctx.Entries.Values)
		{
			if (entry.IsGlobal || !ctx.Definitions.TryGetValue(entry.Id, out Definition def))
			{
				continue;
			}

			ElementFilter scope = familyElements;
			if (ctx.BoundCategories.TryGetValue(entry.Id, out HashSet<long> bound))
			{
				if (bound.Count == 0)
				{
					continue;
				}

				scope = new ElementMulticategoryFilter(bound.Select(c => new ElementId(c)).ToList());
			}

			entry.HasValues = AnyValue(ctx.Doc, entry.Id, def, scope);
		}
	}

	private static bool AnyValue(Document doc, long parameterId, Definition def, ElementFilter scope)
	{
		try
		{
			var hasValue = new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(new ElementId(parameterId)));
			using (var collector = new FilteredElementCollector(doc).WherePasses(scope).WherePasses(hasValue))
			{
				// El filtro nativo ya dejó solo los que tienen valor; se descartan los textos vacíos.
				foreach (Element element in collector)
				{
					if (Filled(element, def))
					{
						return true;
					}
				}
			}

			return false;
		}
		catch (Exception)
		{
			// Si Revit no acepta la regla para este parámetro, se revisa elemento por elemento.
			try
			{
				using (var collector = new FilteredElementCollector(doc).WherePasses(scope))
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
	}

	private static void FindWarnings(Context ctx)
	{
		foreach (ParamEntry entry in ctx.Entries.Values)
		{
			if (!entry.IsUnused)
			{
				// Lo que está en uso no necesita advertencias de borrado.
				entry.Warnings.Clear();
				continue;
			}

			if (entry.Binding == ParamEntry.FamiliesOnly)
			{
				entry.Warnings.Add("Sin enlace a categorías: viene de familias cargadas (reaparece al recargarlas)");
			}

			if (ctx.BoundCategories.TryGetValue(entry.Id, out HashSet<long> bound) && bound.Overlaps(SheetLikeCategories))
			{
				entry.Warnings.Add("Enlazado a planos/vistas/cajetín/info. de proyecto: puede ser una etiqueta del cajetín aunque esté vacío");
			}
		}
	}

	/// <summary>El parámetro existe en el elemento y tiene información (un texto vacío no cuenta).</summary>
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
				case StorageType.ElementId:
					return parameter.AsElementId() != ElementId.InvalidElementId;
				case StorageType.Integer:
				case StorageType.Double:
					return true;
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
