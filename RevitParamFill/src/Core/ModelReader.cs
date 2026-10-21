using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitParamFill.Core;

/// <summary>Lee de los elementos los datos de origen de las reglas (con caché por tipo).</summary>
public sealed class ModelReader
{
	private readonly Document _doc;
	private readonly Dictionary<long, Element> _types = new Dictionary<long, Element>();
	private readonly Dictionary<long, string> _names = new Dictionary<long, string>();
	private readonly Dictionary<long, string> _typeMaterial = new Dictionary<long, string>();

	public ModelReader(Document doc)
	{
		_doc = doc;
	}

	/// <summary>Elementos de modelo a revisar (no tipos, no de vista, con categoría que admite parámetros).</summary>
	public static IEnumerable<Element> ModelElements(Document doc) =>
		new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e =>
			e.Category != null && e.Category.CategoryType == CategoryType.Model && e.Category.AllowsBoundParameters && !e.ViewSpecific);

	public Element TypeOf(Element element)
	{
		ElementId typeId = element.GetTypeId();
		if (typeId == ElementId.InvalidElementId)
		{
			return null;
		}

		if (!_types.TryGetValue(typeId.Value, out Element type))
		{
			type = _doc.GetElement(typeId);
			_types[typeId.Value] = type;
		}

		return type;
	}

	public ElementInfo Info(Element element)
	{
		Element type = TypeOf(element);
		var info = new ElementInfo
		{
			Id = element.Id.Value,
			TypeId = type?.Id.Value ?? -1,
			Category = element.Category?.Name ?? string.Empty,
			Family = type is ElementType elementType ? elementType.FamilyName ?? string.Empty : string.Empty,
			TypeName = type?.Name ?? element.Name ?? string.Empty,
			Level = LevelName(element) ?? string.Empty
		};

		XYZ point = null;
		if (element.Location is LocationPoint locationPoint)
		{
			point = locationPoint.Point;
		}
		else if (element.Location is LocationCurve locationCurve && locationCurve.Curve != null)
		{
			point = locationCurve.Curve.Evaluate(0.5, true);
		}

		BoundingBoxXYZ box = element.get_BoundingBox(null);
		if (point == null && box != null)
		{
			point = (box.Min + box.Max) / 2;
		}

		if (box != null)
		{
			info.BoxMinX = box.Min.X;
			info.BoxMinY = box.Min.Y;
			info.BoxMaxX = box.Max.X;
			info.BoxMaxY = box.Max.Y;
			info.HasBox = true;
		}

		// Lineales: puntos cada ~15 cm a lo largo de la curva (para repartir su longitud entre sectores).
		if (element.Location is LocationCurve curveLocation && curveLocation.Curve != null && curveLocation.Curve.IsBound)
		{
			Curve curve = curveLocation.Curve;
			int count = (int)Math.Max(2, Math.Min(400, Math.Ceiling(curve.Length / 0.5)));
			info.CurvePoints = new List<(double X, double Y)>(count);
			for (int i = 0; i < count; i++)
			{
				XYZ p = curve.Evaluate((double)i / (count - 1), true);
				info.CurvePoints.Add((p.X, p.Y));
			}
		}

		if (point != null)
		{
			info.X = point.X;
			info.Y = point.Y;
			info.Z = point.Z;
			info.MinZ = box?.Min.Z ?? point.Z;
			info.HasLocation = true;
		}

		return info;
	}

	/// <summary>El valor directo del origen de la regla (sin mapa ni prefijo), o null si no hay dato.</summary>
	public string Read(Element element, FillRule rule)
	{
		Element type = TypeOf(element);
		switch (rule.Source)
		{
			case SourceKind.Categoria:
				return element.Category?.Name;
			case SourceKind.Familia:
				return (type as ElementType)?.FamilyName;
			case SourceKind.Tipo:
				return type?.Name;
			case SourceKind.FamiliaTipo:
				return type is ElementType et ? et.FamilyName + " : " + et.Name : null;
			case SourceKind.Marca:
				return Value(element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK));
			case SourceKind.MarcaTipo:
				return Value(type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK));
			case SourceKind.Fabricante:
				return Value(type?.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER)) ?? Value(element.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER));
			case SourceKind.Modelo:
				return Value(type?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)) ?? Value(element.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL));
			case SourceKind.Nivel:
				return LevelName(element);
			case SourceKind.Material:
				return MaterialName(element, type);
			case SourceKind.Parametro:
				if (string.IsNullOrWhiteSpace(rule.Detail))
				{
					return null;
				}

				return Value(element.LookupParameter(rule.Detail)) ?? Value(type?.LookupParameter(rule.Detail));
			case SourceKind.Fijo:
				return string.IsNullOrEmpty(rule.Detail) ? null : rule.Detail;
			default:
				return null;
		}
	}

	/// <summary>Texto del valor de un parámetro (elementos referenciados por su nombre).</summary>
	public string Value(Parameter parameter)
	{
		if (parameter == null || !parameter.HasValue)
		{
			return null;
		}

		string value;
		switch (parameter.StorageType)
		{
			case StorageType.String:
				value = parameter.AsString();
				break;
			case StorageType.ElementId:
				ElementId id = parameter.AsElementId();
				value = id == null || id == ElementId.InvalidElementId ? null : Name(id);
				break;
			default:
				value = parameter.AsValueString();
				break;
		}

		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private string Name(ElementId id)
	{
		if (!_names.TryGetValue(id.Value, out string name))
		{
			name = _doc.GetElement(id)?.Name;
			_names[id.Value] = name;
		}

		return name;
	}

	public string LevelName(Element element)
	{
		ElementId levelId = element.LevelId;
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			Parameter parameter = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT) ??
				element.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
			levelId = parameter?.StorageType == StorageType.ElementId ? parameter.AsElementId() : null;
		}

		return levelId == null || levelId == ElementId.InvalidElementId ? null : Name(levelId);
	}

	/// <summary>
	/// El material principal: un parámetro de material del ejemplar o del tipo; si no, la capa más gruesa de su estructura (muros,
	/// suelos, techos, cubiertas); si no, el material con más área del elemento.
	/// </summary>
	public string MaterialName(Element element, Element type)
	{
		string fromInstance = MaterialParameter(element);
		if (fromInstance != null)
		{
			return fromInstance;
		}

		if (type != null && _typeMaterial.TryGetValue(type.Id.Value, out string cached) && cached != null)
		{
			return cached;
		}

		string fromType = type == null ? null : MaterialParameter(type);
		if (fromType == null && type is HostObjAttributes host)
		{
			CompoundStructure structure = host.GetCompoundStructure();
			CompoundStructureLayer layer = structure?.GetLayers().Where(l => l.MaterialId != ElementId.InvalidElementId)
				.OrderByDescending(l => l.Width).FirstOrDefault();
			if (layer != null)
			{
				fromType = Name(layer.MaterialId);
			}
		}

		if (type != null && fromType != null)
		{
			_typeMaterial[type.Id.Value] = fromType;
			return fromType;
		}

		try
		{
			ElementId best = element.GetMaterialIds(false).OrderByDescending(id => element.GetMaterialArea(id, false)).FirstOrDefault();
			return best == null ? null : Name(best);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private string MaterialParameter(Element element)
	{
		Parameter structural = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) ?? element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
		string value = Value(structural);
		if (value != null)
		{
			return value;
		}

		foreach (Parameter parameter in element.Parameters)
		{
			if (parameter.StorageType == StorageType.ElementId && parameter.HasValue && parameter.Definition != null &&
				parameter.Definition.GetDataType() == SpecTypeId.Reference.Material)
			{
				value = Value(parameter);
				if (value != null)
				{
					return value;
				}
			}
		}

		return null;
	}

	/// <summary>El parámetro destino: de ejemplar si existe; si no, del tipo.</summary>
	public Parameter Target(Element element, string name, out bool onType)
	{
		onType = false;
		Parameter parameter = element.LookupParameter(name);
		if (parameter != null)
		{
			return parameter;
		}

		parameter = TypeOf(element)?.LookupParameter(name);
		onType = parameter != null;
		return parameter;
	}

	/// <summary>Escribe un texto en un parámetro según su tipo de dato.</summary>
	public static bool Set(Parameter parameter, string value)
	{
		if (parameter == null || parameter.IsReadOnly)
		{
			return false;
		}

		value ??= string.Empty;
		switch (parameter.StorageType)
		{
			case StorageType.String:
				return parameter.Set(value);
			case StorageType.Integer:
			case StorageType.Double:
				return value.Length > 0 && parameter.SetValueString(value);
			default:
				return false;
		}
	}
}
