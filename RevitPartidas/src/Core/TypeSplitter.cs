using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>Un tipo con elementos en varios valores del filtro (p. ej. ETAPA 1 y ETAPA 2).</summary>
public sealed class SharedType
{
	public long TypeId;

	public string Category;

	public string FamilyType;

	public string TypeName;

	public string AssemblyCode;

	/// <summary>Valor del filtro → elementos con ese valor.</summary>
	public Dictionary<string, List<long>> ByValue = new Dictionary<string, List<long>>(StringComparer.Ordinal);

	/// <summary>Se puede duplicar (tipo de familia o de sistema que admite Duplicate).</summary>
	public bool CanDuplicate;
}

/// <summary>Duplicar un tipo para los elementos de un valor del filtro y pasarlos al tipo nuevo.</summary>
public sealed class SplitRequest
{
	public long TypeId;

	public string Value;

	public string NewName;

	public List<long> Elements = new List<long>();

	/// <summary>Código de montaje del tipo nuevo (null = el mismo que el original).</summary>
	public string AssemblyCode;
}

/// <summary>
/// El Código de montaje es de tipo y la etapa de ejemplar: un tipo con elementos en dos etapas no puede llevar el código de
/// las dos. Aquí se encuentran esos tipos y se duplican para que cada etapa tenga el suyo.
/// </summary>
public static class TypeSplitter
{
	/// <summary>El primer nivel del filtro que sale de un parámetro de Revit (p. ej. la etapa), o -1.</summary>
	public static int FilterLevel(PartidasConfig config) =>
		Enumerable.Range(0, config.Levels).Cast<int?>()
			.FirstOrDefault(i => config.Link(i.Value).Mode == LinkMode.Parametro && !string.IsNullOrEmpty(config.Link(i.Value).ParameterName)) ?? -1;

	public static List<SharedType> Find(Document doc, PartidasConfig config)
	{
		var result = new List<SharedType>();
		int level = FilterLevel(config);
		if (level < 0)
		{
			return result;
		}

		LevelLink link = config.Link(level);
		var types = new Dictionary<long, SharedType>();
		foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
		{
			Category category = element.Category;
			ElementId typeId = element.GetTypeId();
			if (category == null || category.CategoryType != CategoryType.Model || element.ViewSpecific || typeId == ElementId.InvalidElementId)
			{
				continue;
			}

			// Solo el valor de ejemplar: si sale del tipo, el tipo no puede estar en dos etapas.
			Parameter parameter = element.LookupParameter(link.ParameterName);
			if (parameter == null || !parameter.HasValue)
			{
				continue;
			}

			string value = (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString())?.Trim();
			if (string.IsNullOrEmpty(value) || (link.ValueMap.TryGetValue(BudgetReader.Simplify(value), out string rule) && rule.Length == 0))
			{
				continue;
			}

			if (!types.TryGetValue(typeId.Value, out SharedType shared))
			{
				Element type = doc.GetElement(typeId);
				shared = new SharedType
				{
					TypeId = typeId.Value,
					Category = category.Name,
					TypeName = type?.Name ?? string.Empty,
					FamilyType = type is ElementType elementType ? elementType.FamilyName + " : " + elementType.Name : type?.Name ?? string.Empty,
					AssemblyCode = AssemblyCodes.Code(type),
					CanDuplicate = type is ElementType
				};
				types[typeId.Value] = shared;
			}

			if (!shared.ByValue.TryGetValue(value, out List<long> ids))
			{
				shared.ByValue[value] = ids = new List<long>();
			}

			ids.Add(element.Id.Value);
		}

		result.AddRange(types.Values.Where(t => t.ByValue.Count > 1)
			.OrderBy(t => t.Category, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(t => t.FamilyType, StringComparer.CurrentCultureIgnoreCase));
		return result;
	}

	/// <summary>Elementos que se intentan cambiar juntos cuando el cambio en bloque falla.</summary>
	private const int BatchSize = 200;

	/// <summary>
	/// Duplica los tipos y cambia sus elementos. Debe llamarse dentro de una transacción. Para ir rápido: los tipos que ya existen
	/// se buscan en un índice armado una sola vez, y los elementos se cambian de tipo en bloque (una sola regeneración por tipo).
	/// </summary>
	public static string Split(Document doc, IList<SplitRequest> requests)
	{
		DateTime start = DateTime.Now;
		int created = 0;
		int reused = 0;
		int moved = 0;
		int failedTypes = 0;
		int failedElements = 0;
		int inGroups = 0;
		Dictionary<(Type, string, string), ElementType> index = TypeIndex(doc);
		foreach (SplitRequest request in requests)
		{
			if (!(doc.GetElement(new ElementId(request.TypeId)) is ElementType original))
			{
				failedTypes++;
				continue;
			}

			string name = request.NewName.Trim();
			var key = (original.GetType(), original.FamilyName, name);
			if (index.TryGetValue(key, out ElementType target))
			{
				reused++;
			}
			else
			{
				try
				{
					target = original.Duplicate(name);
					index[key] = target;
					created++;
				}
				catch (Exception)
				{
					failedTypes++;
					continue;
				}
			}

			if (target == null)
			{
				failedTypes++;
				continue;
			}

			if (request.AssemblyCode != null)
			{
				Parameter code = AssemblyCodes.CodeParameter(target);
				try
				{
					if (code != null && !code.IsReadOnly)
					{
						code.Set(request.AssemblyCode);
					}
				}
				catch (Exception)
				{
					// El tipo queda creado aunque no admita código de montaje.
				}
			}

			// Se descartan antes los que no pueden cambiar (en grupos) o ya tienen el tipo: así el bloque no falla por ellos.
			var ids = new List<ElementId>();
			foreach (long id in request.Elements)
			{
				Element element = doc.GetElement(new ElementId(id));
				if (element == null || element.GetTypeId() == target.Id)
				{
					continue;
				}

				if (element.GroupId != ElementId.InvalidElementId)
				{
					inGroups++;
					continue;
				}

				ids.Add(element.Id);
			}

			int changed = ChangeTypes(doc, ids, target.Id);
			moved += changed;
			failedElements += ids.Count - changed;
		}

		string text = $"Tipos nuevos: {created}" + (reused > 0 ? $" (y {reused} que ya existían con ese nombre)" : string.Empty) +
			$". Elementos pasados a su tipo de etapa: {moved}, en {(DateTime.Now - start).TotalSeconds:0.0} s.";
		if (failedTypes > 0)
		{
			text += $" {failedTypes} tipo(s) no se pudieron duplicar.";
		}

		if (inGroups > 0)
		{
			text += $" {inGroups} elemento(s) están en grupos y no se cambiaron (edita el grupo).";
		}

		if (failedElements > 0)
		{
			text += $" {failedElements} elemento(s) no se pudieron cambiar de tipo.";
		}

		return text + " Vuelve a analizar en el paso 3.";
	}

	/// <summary>Cambia de tipo todos a la vez; si Revit rechaza el bloque, por tandas y, dentro de una tanda que falla, uno a uno.</summary>
	private static int ChangeTypes(Document doc, List<ElementId> ids, ElementId typeId)
	{
		if (ids.Count == 0)
		{
			return 0;
		}

		if (TryChange(doc, ids, typeId))
		{
			return ids.Count;
		}

		int changed = 0;
		for (int i = 0; i < ids.Count; i += BatchSize)
		{
			List<ElementId> batch = ids.GetRange(i, Math.Min(BatchSize, ids.Count - i));
			if (batch.Count > 1 && TryChange(doc, batch, typeId))
			{
				changed += batch.Count;
				continue;
			}

			foreach (ElementId id in batch)
			{
				if (TryChange(doc, new List<ElementId> { id }, typeId))
				{
					changed++;
				}
			}
		}

		return changed;
	}

	private static bool TryChange(Document doc, ICollection<ElementId> ids, ElementId typeId)
	{
		try
		{
			Element.ChangeTypeId(doc, ids, typeId);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Todos los tipos del modelo por (clase, familia, nombre), en un solo recorrido.</summary>
	private static Dictionary<(Type, string, string), ElementType> TypeIndex(Document doc)
	{
		var index = new Dictionary<(Type, string, string), ElementType>();
		foreach (ElementType type in new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>())
		{
			var key = (type.GetType(), type.FamilyName ?? string.Empty, type.Name ?? string.Empty);
			if (!index.ContainsKey(key))
			{
				index[key] = type;
			}
		}

		return index;
	}
}
