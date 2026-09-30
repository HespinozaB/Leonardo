using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Tools;

internal static class RevitHelpers
{
	public static UIDocument ActiveUiDoc(UIApplication app)
	{
		return app.ActiveUIDocument ?? throw new InvalidOperationException("No hay ningún documento abierto en Revit.");
	}

	public static Document ActiveDoc(UIApplication app) => ActiveUiDoc(app).Document;

	// ---------- Unidades (la API trabaja en pies; la interfaz MCP en milímetros) ----------

	public static double Mm(double millimeters) => UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

	public static double ToMm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 3);

	public static XYZ PointMm(double x, double y, double z = 0) => new XYZ(Mm(x), Mm(y), Mm(z));

	public static Dictionary<string, object> PointToMm(XYZ p) => new Dictionary<string, object>
	{
		["x"] = ToMm(p.X),
		["y"] = ToMm(p.Y),
		["z"] = ToMm(p.Z)
	};

	// ---------- Identificadores ----------

	public static ElementId Id(long value) => new ElementId(value);

	public static Element RequireElement(Document doc, long id)
	{
		return doc.GetElement(Id(id)) ?? throw new ArgumentException("No existe el elemento con id " + id + ".");
	}

	// ---------- Categorías: acepta "OST_Walls", "Walls" o el nombre localizado ("Muros") ----------

	public static BuiltInCategory ResolveCategory(Document doc, string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Categoría vacía.");
		}
		string trimmed = name.Trim();
		string candidate = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? trimmed : "OST_" + trimmed;
		if (Enum.TryParse(candidate, true, out BuiltInCategory bic) && bic != BuiltInCategory.INVALID)
		{
			return bic;
		}
		foreach (Category cat in doc.Settings.Categories)
		{
			if (Matches(cat.Name, trimmed))
			{
				return (BuiltInCategory)cat.Id.Value;
			}
			foreach (Category sub in cat.SubCategories)
			{
				if (Matches(sub.Name, trimmed))
				{
					return (BuiltInCategory)sub.Id.Value;
				}
			}
		}
		throw new ArgumentException("Categoría no reconocida: '" + name + "'. Usa listar_categorias para ver los nombres válidos.");
	}

	public static bool Matches(string a, string b)
	{
		return string.Compare(
			a?.Trim(), b?.Trim(), CultureInfo.CurrentCulture,
			CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;
	}

	// ---------- Niveles y tipos ----------

	public static Level ResolveLevel(Document doc, string nameOrId)
	{
		List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
		if (levels.Count == 0)
		{
			throw new InvalidOperationException("El proyecto no tiene niveles.");
		}
		if (string.IsNullOrWhiteSpace(nameOrId))
		{
			return levels.OrderBy(l => l.Elevation).First();
		}
		if (long.TryParse(nameOrId, out long id))
		{
			Level byId = levels.FirstOrDefault(l => l.Id.Value == id);
			if (byId != null)
			{
				return byId;
			}
		}
		return levels.FirstOrDefault(l => Matches(l.Name, nameOrId))
			?? throw new ArgumentException("Nivel no encontrado: '" + nameOrId + "'. Disponibles: " + string.Join(", ", levels.Select(l => l.Name)));
	}

	/// <summary>Busca un tipo por id o nombre ("Tipo" o "Familia : Tipo"); si no se indica, devuelve el primero.</summary>
	public static T ResolveType<T>(Document doc, string nameOrId, BuiltInCategory? category = null) where T : ElementType
	{
		var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
		if (category.HasValue)
		{
			collector = collector.OfCategory(category.Value);
		}
		List<T> types = collector.Cast<T>().ToList();
		if (types.Count == 0)
		{
			throw new InvalidOperationException("No hay tipos disponibles de " + typeof(T).Name + " en el proyecto.");
		}
		if (string.IsNullOrWhiteSpace(nameOrId))
		{
			return types.First();
		}
		if (long.TryParse(nameOrId, out long id))
		{
			T byId = types.FirstOrDefault(t => t.Id.Value == id);
			if (byId != null)
			{
				return byId;
			}
		}
		return types.FirstOrDefault(t => Matches(t.Name, nameOrId) || Matches(t.FamilyName + " : " + t.Name, nameOrId))
			?? types.FirstOrDefault(t => (t.FamilyName + " : " + t.Name).IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0)
			?? throw new ArgumentException("Tipo no encontrado: '" + nameOrId + "'. Usa listar_tipos para ver los disponibles.");
	}

	// ---------- Descripción de elementos ----------

	public static Dictionary<string, object> Describe(Document doc, Element e)
	{
		var info = new Dictionary<string, object>
		{
			["id"] = e.Id.Value,
			["nombre"] = e.Name,
			["categoria"] = e.Category?.Name,
			["clase"] = e.GetType().Name
		};
		ElementId typeId = e.GetTypeId();
		if (typeId != null && typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is ElementType type)
		{
			info["tipo"] = type.FamilyName + " : " + type.Name;
			info["tipo_id"] = typeId.Value;
		}
		if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
		{
			info["nivel"] = doc.GetElement(e.LevelId)?.Name;
		}
		switch (e.Location)
		{
			case LocationPoint lp:
				info["punto_mm"] = PointToMm(lp.Point);
				break;
			case LocationCurve lc:
				info["inicio_mm"] = PointToMm(lc.Curve.GetEndPoint(0));
				info["fin_mm"] = PointToMm(lc.Curve.GetEndPoint(1));
				info["longitud_mm"] = ToMm(lc.Curve.Length);
				break;
		}
		return info;
	}

	// ---------- Parámetros ----------

	public static Parameter FindParameter(Element e, string name)
	{
		Parameter p = e.LookupParameter(name);
		if (p != null)
		{
			return p;
		}
		string candidate = name.Trim();
		if (Enum.TryParse(candidate, true, out BuiltInParameter bip) && bip != BuiltInParameter.INVALID)
		{
			p = e.get_Parameter(bip);
			if (p != null)
			{
				return p;
			}
		}
		foreach (Parameter each in e.Parameters)
		{
			if (Matches(each.Definition?.Name, name))
			{
				return each;
			}
		}
		return null;
	}

	public static Dictionary<string, object> ParameterInfo(Parameter p)
	{
		var info = new Dictionary<string, object>
		{
			["nombre"] = p.Definition?.Name,
			["tipo_almacenamiento"] = p.StorageType.ToString(),
			["solo_lectura"] = p.IsReadOnly,
			["valor"] = p.AsValueString() ?? p.AsString()
		};
		if (p.Definition is InternalDefinition def && def.BuiltInParameter != BuiltInParameter.INVALID)
		{
			info["builtin"] = def.BuiltInParameter.ToString();
		}
		switch (p.StorageType)
		{
			case StorageType.Double:
				info["valor_interno"] = p.AsDouble();
				break;
			case StorageType.Integer:
				info["valor_interno"] = p.AsInteger();
				break;
			case StorageType.ElementId:
				info["valor_interno"] = p.AsElementId()?.Value;
				break;
		}
		return info;
	}

	public static void SetParameter(Parameter p, object value)
	{
		if (p.IsReadOnly)
		{
			throw new InvalidOperationException("El parámetro '" + p.Definition?.Name + "' es de solo lectura.");
		}
		string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
		bool ok;
		switch (p.StorageType)
		{
			case StorageType.String:
				ok = p.Set(text);
				break;
			case StorageType.Integer:
				ok = p.Set(ParseInt(value, text));
				break;
			case StorageType.ElementId:
				ok = p.Set(new ElementId((long)Bridge.Args.ToDouble(value)));
				break;
			case StorageType.Double:
				// Primero como texto con unidades del proyecto ("3000", "3 m", "2,5"); si no, número en unidades de visualización.
				ok = p.SetValueString(text);
				if (!ok)
				{
					double number = Bridge.Args.ToDouble(value);
					double internalValue;
					try
					{
						internalValue = UnitUtils.ConvertToInternalUnits(number, p.GetUnitTypeId());
					}
					catch
					{
						internalValue = number;
					}
					ok = p.Set(internalValue);
				}
				break;
			default:
				throw new InvalidOperationException("Tipo de parámetro no soportado.");
		}
		if (!ok)
		{
			throw new InvalidOperationException("Revit rechazó el valor '" + text + "' para '" + p.Definition?.Name + "'.");
		}
	}

	private static int ParseInt(object value, string text)
	{
		if (value is bool b)
		{
			return b ? 1 : 0;
		}
		switch (text.Trim().ToLowerInvariant())
		{
			case "true":
			case "si":
			case "sí":
			case "yes":
				return 1;
			case "false":
			case "no":
				return 0;
		}
		return (int)Bridge.Args.ToDouble(value);
	}

	// ---------- Transacciones que no se bloquean por avisos ----------

	public static Dictionary<string, object> RunTransaction(Document doc, string name, Func<object> action)
	{
		var swallower = new WarningCollector();
		using var t = new Transaction(doc, "MCP: " + name);
		FailureHandlingOptions options = t.GetFailureHandlingOptions();
		options.SetFailuresPreprocessor(swallower);
		options.SetClearAfterRollback(true);
		t.SetFailureHandlingOptions(options);
		t.Start();
		object result;
		try
		{
			result = action();
		}
		catch
		{
			if (t.HasStarted() && !t.HasEnded())
			{
				t.RollBack();
			}
			throw;
		}
		TransactionStatus status = t.Commit();
		if (status != TransactionStatus.Committed)
		{
			throw new InvalidOperationException("La transacción no se confirmó (" + status + "). " + string.Join(" | ", swallower.Messages));
		}
		var response = new Dictionary<string, object> { ["resultado"] = result };
		if (swallower.Messages.Count > 0)
		{
			response["avisos"] = swallower.Messages;
		}
		return response;
	}

	private sealed class WarningCollector : IFailuresPreprocessor
	{
		public List<string> Messages { get; } = new List<string>();

		public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
		{
			foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
			{
				Messages.Add(message.GetDescriptionText());
				if (message.GetSeverity() == FailureSeverity.Warning)
				{
					accessor.DeleteWarning(message);
				}
			}
			return FailureProcessingResult.Continue;
		}
	}
}
