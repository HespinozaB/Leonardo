using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal static class EditTools
{
	/// <summary>
	/// <c>updates: [{ ids, parameter, value, onType? }]</c>. Un número en un parámetro de longitud,
	/// área, etc. se interpreta en las unidades de visualización de ese parámetro (mm, m², …); un
	/// texto se interpreta como lo escribiría el usuario en Revit ("3000", "3 m").
	/// </summary>
	public static object SetParameters(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		if (args["updates"] is not JsonArray updates || updates.Count == 0)
		{
			throw new ArgumentException("Falta 'updates': [{ ids, parameter, value }].");
		}

		var results = new List<object>();
		RevitUtil.InTransaction(doc, "Cambiar parámetros", () =>
		{
			foreach (JsonObject update in updates.OfType<JsonObject>())
			{
				string name = Args.RequiredStr(update, "parameter");
				bool onType = Args.Bool(update, "onType", false);
				foreach (ElementId id in Args.RequiredIds(update, "ids"))
				{
					Element target = RevitUtil.Get(doc, id);
					if (onType)
					{
						target = doc.GetElement(target.GetTypeId()) ?? throw new ArgumentException($"El elemento {id.Value} no tiene tipo.");
					}

					Parameter p = Lookup(target, name)
						?? throw new ArgumentException($"El elemento {target.Id.Value} no tiene el parámetro '{name}'.");
					if (p.IsReadOnly)
					{
						throw new ArgumentException($"El parámetro '{name}' del elemento {target.Id.Value} es de solo lectura.");
					}

					Assign(p, update["value"]);
					results.Add(new { id = target.Id.Value, parameter = p.Definition.Name, value = Describe.Value(p) });
				}
			}

			return 0;
		}, out List<string> warnings);

		return new { updated = results, warnings };
	}

	public static object DeleteElements(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		List<ElementId> ids = Args.RequiredIds(args, "ids");
		ICollection<ElementId> deleted = RevitUtil.InTransaction(doc, "Eliminar elementos", () => doc.Delete(ids), out List<string> warnings);
		// Revit también borra los elementos dependientes (etiquetas, cotas, alojados...).
		return new { requested = ids.Count, deletedTotal = deleted.Count, deletedIds = deleted.Select(i => i.Value).ToList(), warnings };
	}

	/// <summary>operation: move | copy | rotate | mirror.</summary>
	public static object TransformElements(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		List<ElementId> ids = Args.RequiredIds(args, "ids");
		string operation = Args.RequiredStr(args, "operation").ToLowerInvariant();

		object result = RevitUtil.InTransaction(doc, "Transformar elementos", () =>
		{
			switch (operation)
			{
				case "move":
					ElementTransformUtils.MoveElements(doc, ids, Args.Point(args, "vector"));
					return (object)new { moved = ids.Count };

				case "copy":
					ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, ids, Args.Point(args, "vector"));
					return new { newIds = copies.Select(i => i.Value).ToList() };

				case "rotate":
				{
					XYZ center = Args.Point(args, "center", false) ?? Center(doc, ids);
					Line axis = Line.CreateBound(center, center + XYZ.BasisZ);
					ElementTransformUtils.RotateElements(doc, ids, axis, Units.DegToRad(Args.RequiredDbl(args, "angleDeg")));
					return new { rotated = ids.Count, centerMm = Units.Mm(center) };
				}

				case "mirror":
				{
					// Plano vertical que pasa por 'center' con normal 'normal' (por defecto, eje X: espejo izquierda/derecha).
					XYZ center = Args.Point(args, "center", false) ?? Center(doc, ids);
					XYZ normal = args["normal"] is JsonObject ? Args.Point(args, "normal").Normalize() : XYZ.BasisX;
					ElementTransformUtils.MirrorElements(doc, ids, Plane.CreateByNormalAndOrigin(normal, center), Args.Bool(args, "copy", true));
					return new { mirrored = ids.Count };
				}

				default:
					throw new ArgumentException("operation debe ser move, copy, rotate o mirror.");
			}
		}, out List<string> warnings);

		return new { result, warnings };
	}

	private static XYZ Center(Document doc, IEnumerable<ElementId> ids)
	{
		BoundingBoxXYZ total = null;
		foreach (ElementId id in ids)
		{
			BoundingBoxXYZ box = doc.GetElement(id)?.get_BoundingBox(null);
			if (box == null)
			{
				continue;
			}

			if (total == null)
			{
				total = new BoundingBoxXYZ { Min = box.Min, Max = box.Max };
			}
			else
			{
				total.Min = new XYZ(Math.Min(total.Min.X, box.Min.X), Math.Min(total.Min.Y, box.Min.Y), Math.Min(total.Min.Z, box.Min.Z));
				total.Max = new XYZ(Math.Max(total.Max.X, box.Max.X), Math.Max(total.Max.Y, box.Max.Y), Math.Max(total.Max.Z, box.Max.Z));
			}
		}

		if (total == null)
		{
			throw new ArgumentException("No se pudo calcular el centro: indica 'center'.");
		}

		XYZ mid = (total.Min + total.Max) / 2;
		return new XYZ(mid.X, mid.Y, total.Min.Z);
	}

	/// <summary>Por nombre visible o por BuiltInParameter ("ALL_MODEL_INSTANCE_COMMENTS").</summary>
	private static Parameter Lookup(Element e, string name)
	{
		Parameter p = e.LookupParameter(name);
		if (p == null && Enum.TryParse(name, true, out BuiltInParameter bip) && !name.All(char.IsDigit))
		{
			p = e.get_Parameter(bip);
		}

		return p;
	}

	private static void Assign(Parameter p, JsonNode value)
	{
		JsonValueKind kind = value?.GetValueKind() ?? JsonValueKind.Null;
		string text = kind == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString();
		bool ok;

		switch (p.StorageType)
		{
			case StorageType.String:
				ok = p.Set(kind == JsonValueKind.Null ? "" : text);
				break;

			case StorageType.Integer:
				if (kind is JsonValueKind.True or JsonValueKind.False)
				{
					ok = p.Set(kind == JsonValueKind.True ? 1 : 0);
				}
				else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
				{
					ok = p.Set(i);
				}
				else
				{
					ok = p.SetValueString(text);
				}

				break;

			case StorageType.Double:
				if (kind == JsonValueKind.Number)
				{
					double number = value.GetValue<double>();
					ForgeTypeId unit = null;
					try
					{
						unit = p.GetUnitTypeId();
					}
					catch (Exception)
					{
						// Parámetro sin unidades (número puro).
					}

					ok = p.Set(unit == null ? number : UnitUtils.ConvertToInternalUnits(number, unit));
				}
				else
				{
					ok = p.SetValueString(text);
				}

				break;

			case StorageType.ElementId:
				ok = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
					? p.Set(new ElementId(id))
					: throw new ArgumentException($"'{p.Definition.Name}' espera el id de un elemento.");
				break;

			default:
				ok = false;
				break;
		}

		if (!ok)
		{
			throw new ArgumentException($"Revit rechazó el valor '{text}' para '{p.Definition.Name}'.");
		}
	}
}
