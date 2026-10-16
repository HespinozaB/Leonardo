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

	/// <summary>Duplica los tipos y cambia sus elementos. Debe llamarse dentro de una transacción.</summary>
	public static string Split(Document doc, IList<SplitRequest> requests)
	{
		int created = 0;
		int reused = 0;
		int moved = 0;
		int failedTypes = 0;
		int failedElements = 0;
		foreach (SplitRequest request in requests)
		{
			if (!(doc.GetElement(new ElementId(request.TypeId)) is ElementType original))
			{
				failedTypes++;
				continue;
			}

			ElementType target = Existing(doc, original, request.NewName);
			if (target != null)
			{
				reused++;
			}
			else
			{
				try
				{
					target = original.Duplicate(request.NewName.Trim());
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

			foreach (long id in request.Elements)
			{
				Element element = doc.GetElement(new ElementId(id));
				try
				{
					if (element != null && element.GetTypeId() != target.Id)
					{
						element.ChangeTypeId(target.Id);
						moved++;
					}
				}
				catch (Exception)
				{
					failedElements++;
				}
			}
		}

		string text = $"Tipos nuevos: {created}" + (reused > 0 ? $" (y {reused} que ya existían con ese nombre)" : string.Empty) +
			$". Elementos pasados a su tipo de etapa: {moved}.";
		if (failedTypes > 0)
		{
			text += $" {failedTypes} tipo(s) no se pudieron duplicar.";
		}

		if (failedElements > 0)
		{
			text += $" {failedElements} elemento(s) no se pudieron cambiar de tipo (en grupos, vínculos o bloqueados).";
		}

		return text + " Vuelve a analizar en el paso 3.";
	}

	/// <summary>Un tipo de la misma familia (o de la misma clase, si es de sistema) que ya tiene ese nombre.</summary>
	private static ElementType Existing(Document doc, ElementType original, string name)
	{
		name = name.Trim();
		if (original is FamilySymbol symbol)
		{
			return symbol.Family.GetFamilySymbolIds().Select(doc.GetElement).OfType<ElementType>()
				.FirstOrDefault(t => t.Name == name);
		}

		IEnumerable<Element> candidates;
		try
		{
			candidates = new FilteredElementCollector(doc).OfClass(original.GetType());
		}
		catch (Exception)
		{
			// Algunas clases no admiten el filtro por clase: se revisan todos los tipos.
			candidates = new FilteredElementCollector(doc).WhereElementIsElementType();
		}

		return candidates.OfType<ElementType>()
			.FirstOrDefault(t => t.Name == name && t.FamilyName == original.FamilyName && t.GetType() == original.GetType());
	}
}
