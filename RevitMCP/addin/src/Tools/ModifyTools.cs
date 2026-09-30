using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;
using static RevitMCP.Tools.RevitHelpers;

namespace RevitMCP.Tools;

/// <summary>Herramientas que modifican el modelo. Todas las coordenadas y longitudes van en milímetros.</summary>
internal static class ModifyTools
{
	public static object SetParameter(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		List<long> ids = a.Ids("ids");
		string name = a.ReqStr("parametro");
		object value = a.Raw("valor") ?? throw new ArgumentException("Falta el parámetro obligatorio 'valor'.");
		bool onType = a.Bool("en_tipo", false);

		return RunTransaction(doc, "Establecer " + name, () =>
		{
			var changed = new List<Dictionary<string, object>>();
			foreach (long id in ids)
			{
				Element e = RequireElement(doc, id);
				Element target = onType ? doc.GetElement(e.GetTypeId()) ?? e : e;
				Parameter p = FindParameter(target, name)
					?? throw new ArgumentException("El elemento " + id + " no tiene el parámetro '" + name + "'.");
				RevitHelpers.SetParameter(p, value);
				changed.Add(new Dictionary<string, object> { ["id"] = target.Id.Value, ["parametro"] = ParameterInfo(p) });
			}
			return changed;
		});
	}

	public static object CreateLevel(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		double elevation = a.ReqNum("elevacion_mm");
		string name = a.Str("nombre");
		return RunTransaction(doc, "Crear nivel", () =>
		{
			Level level = Level.Create(doc, Mm(elevation));
			if (!string.IsNullOrWhiteSpace(name))
			{
				level.Name = name;
			}
			return new Dictionary<string, object> { ["id"] = level.Id.Value, ["nombre"] = level.Name };
		});
	}

	public static object CreateWall(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		Level level = ResolveLevel(doc, a.Str("nivel"));
		WallType type = ResolveType<WallType>(doc, a.Str("tipo"));
		var start = new XYZ(Mm(a.ReqNum("x1")), Mm(a.ReqNum("y1")), level.Elevation);
		var end = new XYZ(Mm(a.ReqNum("x2")), Mm(a.ReqNum("y2")), level.Elevation);
		double height = Mm(a.Num("altura_mm", 3000));
		double offset = Mm(a.Num("desfase_base_mm", 0));
		bool structural = a.Bool("estructural", false);

		return RunTransaction(doc, "Crear muro", () =>
		{
			Wall wall = Wall.Create(doc, Line.CreateBound(start, end), type.Id, level.Id, height, offset, false, structural);
			return Describe(doc, wall);
		});
	}

	public static object CreateFloor(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		Level level = ResolveLevel(doc, a.Str("nivel"));
		FloorType type = ResolveType<FloorType>(doc, a.Str("tipo"), BuiltInCategory.OST_Floors);
		List<XYZ> points = a.List("puntos").Select(ToPoint).Select(p => new XYZ(p.X, p.Y, level.Elevation)).ToList();
		if (points.Count < 3)
		{
			throw new ArgumentException("'puntos' necesita al menos 3 vértices [x, y] en mm.");
		}

		return RunTransaction(doc, "Crear suelo", () =>
		{
			var loop = new CurveLoop();
			for (int i = 0; i < points.Count; i++)
			{
				loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
			}
			Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, type.Id, level.Id);
			return Describe(doc, floor);
		});
	}

	public static object PlaceFamily(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		FamilySymbol symbol = ResolveType<FamilySymbol>(doc, a.ReqStr("tipo"));
		Level level = ResolveLevel(doc, a.Str("nivel"));
		XYZ point = PointMm(a.ReqNum("x"), a.ReqNum("y"), a.Num("z", 0));
		point = new XYZ(point.X, point.Y, point.Z + level.Elevation);
		double rotation = a.Num("rotacion_grados", 0);
		long hostId = (long)a.Num("anfitrion_id", -1);

		return RunTransaction(doc, "Colocar familia", () =>
		{
			if (!symbol.IsActive)
			{
				symbol.Activate();
				doc.Regenerate();
			}
			FamilyInstance instance;
			if (hostId > 0)
			{
				// Puertas, ventanas y otras familias alojadas en un muro/anfitrión.
				Element host = RequireElement(doc, hostId);
				instance = doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
			}
			else
			{
				instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
			}
			if (Math.Abs(rotation) > 1e-9)
			{
				Line axis = Line.CreateBound(point, point + XYZ.BasisZ);
				ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * Math.PI / 180.0);
			}
			return Describe(doc, instance);
		});
	}

	public static object CreateRoom(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		Level level = ResolveLevel(doc, a.Str("nivel"));
		var uv = new UV(Mm(a.ReqNum("x")), Mm(a.ReqNum("y")));
		string name = a.Str("nombre");
		string number = a.Str("numero");

		return RunTransaction(doc, "Crear habitación", () =>
		{
			var room = doc.Create.NewRoom(level, uv);
			if (!string.IsNullOrWhiteSpace(name))
			{
				room.Name = name;
			}
			if (!string.IsNullOrWhiteSpace(number))
			{
				room.Number = number;
			}
			var info = Describe(doc, room);
			info["area_m2"] = Math.Round(UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters), 3);
			return info;
		});
	}

	public static object CreatePlanView(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		Level level = ResolveLevel(doc, a.Str("nivel"));
		ViewFamily family = a.Bool("techo", false) ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
		ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
			.FirstOrDefault(t => t.ViewFamily == family)
			?? throw new InvalidOperationException("No hay tipo de vista " + family + " en el proyecto.");
		string name = a.Str("nombre");

		return RunTransaction(doc, "Crear vista de planta", () =>
		{
			ViewPlan view = ViewPlan.Create(doc, vft.Id, level.Id);
			if (!string.IsNullOrWhiteSpace(name))
			{
				view.Name = name;
			}
			return new Dictionary<string, object> { ["id"] = view.Id.Value, ["nombre"] = view.Name };
		});
	}

	public static object Move(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		List<ElementId> ids = a.Ids("ids").Select(Id).ToList();
		XYZ delta = PointMm(a.Num("dx", 0), a.Num("dy", 0), a.Num("dz", 0));
		return RunTransaction(doc, "Mover elementos", () =>
		{
			ElementTransformUtils.MoveElements(doc, ids, delta);
			return ids.Count;
		});
	}

	public static object Copy(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		List<ElementId> ids = a.Ids("ids").Select(Id).ToList();
		XYZ delta = PointMm(a.Num("dx", 0), a.Num("dy", 0), a.Num("dz", 0));
		return RunTransaction(doc, "Copiar elementos", () =>
			ElementTransformUtils.CopyElements(doc, ids, delta).Select(id => id.Value).ToList());
	}

	public static object Delete(UIApplication app, Args a)
	{
		Document doc = ActiveDoc(app);
		List<ElementId> ids = a.Ids("ids").Select(Id).ToList();
		return RunTransaction(doc, "Eliminar elementos", () =>
		{
			ICollection<ElementId> deleted = doc.Delete(ids);
			return new Dictionary<string, object>
			{
				["solicitados"] = ids.Count,
				["eliminados_total"] = deleted.Count  // incluye dependientes (etiquetas, cotas...)
			};
		});
	}

	private static XYZ ToPoint(object raw)
	{
		List<double> values = raw is System.Collections.IEnumerable list && !(raw is string)
			? list.Cast<object>().Select(Args.ToDouble).ToList()
			: throw new ArgumentException("Cada punto debe ser [x, y] en mm.");
		if (values.Count < 2)
		{
			throw new ArgumentException("Cada punto debe ser [x, y] en mm.");
		}
		return PointMm(values[0], values[1]);
	}
}
