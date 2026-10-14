using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal static class CreateTools
{
	public static object CreateLevel(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		double elevation = Units.ToInternal(Args.RequiredDbl(args, "elevationMm"));
		string name = Args.Str(args, "name");
		bool withPlan = Args.Bool(args, "createPlanView", true);

		object result = RevitUtil.InTransaction(doc, "Crear nivel", () =>
		{
			Level level = Level.Create(doc, elevation);
			if (!string.IsNullOrWhiteSpace(name))
			{
				level.Name = name;
			}

			long? planId = null;
			if (withPlan)
			{
				ViewFamilyType planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
					.FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
				if (planType != null)
				{
					planId = ViewPlan.Create(doc, planType.Id, level.Id).Id.Value;
				}
			}

			return new { id = level.Id.Value, name = level.Name, floorPlanViewId = planId };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	public static object CreateGrid(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		Line line = Line.CreateBound(Flat(Args.Point(args, "start")), Flat(Args.Point(args, "end")));
		string name = Args.Str(args, "name");

		object result = RevitUtil.InTransaction(doc, "Crear rejilla", () =>
		{
			Grid grid = Grid.Create(doc, line);
			if (!string.IsNullOrWhiteSpace(name))
			{
				grid.Name = name;
			}

			return new { id = grid.Id.Value, name = grid.Name };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	/// <summary>Uno o varios muros: <c>walls: [{ start, end }]</c> o un solo <c>start</c>/<c>end</c>.</summary>
	public static object CreateWall(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		Level level = RevitUtil.FindLevel(doc, Args.Str(args, "level"), app.ActiveUIDocument.ActiveView);
		WallType type = RevitUtil.FindType<WallType>(doc, Args.Str(args, "wallType"), ElementTypeGroup.WallType);
		double height = Units.ToInternal(Args.Dbl(args, "heightMm") ?? 3000);
		double offset = Units.ToInternal(Args.Dbl(args, "baseOffsetMm") ?? 0);
		bool structural = Args.Bool(args, "structural", false);

		List<(XYZ Start, XYZ End)> segments;
		if (args["points"] is JsonArray)
		{
			// Polilínea; con closed=true se cierra uniendo el último punto con el primero.
			List<XYZ> points = Args.Points(args, "points");
			int count = Args.Bool(args, "closed", false) ? points.Count : points.Count - 1;
			segments = Enumerable.Range(0, Math.Max(0, count)).Select(i => (points[i], points[(i + 1) % points.Count])).ToList();
		}
		else if (args["walls"] is JsonArray walls)
		{
			segments = walls.OfType<JsonObject>().Select(w => (Args.Point(w, "start"), Args.Point(w, "end"))).ToList();
		}
		else
		{
			segments = new List<(XYZ, XYZ)> { (Args.Point(args, "start"), Args.Point(args, "end")) };
		}

		object result = RevitUtil.InTransaction(doc, "Crear muros", () =>
		{
			var ids = new List<long>();
			foreach ((XYZ start, XYZ end) in segments)
			{
				Line line = Line.CreateBound(Flat(start, level.Elevation), Flat(end, level.Elevation));
				ids.Add(Wall.Create(doc, line, type.Id, level.Id, height, offset, false, structural).Id.Value);
			}

			return new { ids, level = level.Name, wallType = type.Name };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	public static object CreateFloor(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		Level level = RevitUtil.FindLevel(doc, Args.Str(args, "level"), app.ActiveUIDocument.ActiveView);
		FloorType type = RevitUtil.FindType<FloorType>(doc, Args.Str(args, "floorType"), ElementTypeGroup.FloorType);
		List<XYZ> points = Args.Points(args, "points").Select(p => Flat(p, level.Elevation)).ToList();
		if (points.Count < 3)
		{
			throw new ArgumentException("El contorno del suelo necesita al menos 3 puntos.");
		}

		var loop = new CurveLoop();
		for (int i = 0; i < points.Count; i++)
		{
			loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
		}

		object result = RevitUtil.InTransaction(doc, "Crear suelo", () =>
		{
			Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, type.Id, level.Id);
			if (Args.Dbl(args, "offsetMm") is double offsetMm)
			{
				floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(Units.ToInternal(offsetMm));
			}

			return new { id = floor.Id.Value, level = level.Name, floorType = type.Name };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	public static object PlaceFamilyInstance(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		FamilySymbol symbol = RevitUtil.FindSymbol(doc, Args.RequiredStr(args, "familyType"), Args.Str(args, "category"));
		Level level = RevitUtil.FindLevel(doc, Args.Str(args, "level"), app.ActiveUIDocument.ActiveView);
		XYZ point = Args.Point(args, "point");
		ElementId hostId = Args.Id(args, "hostId");
		double rotation = Units.DegToRad(Args.Dbl(args, "rotationDeg") ?? 0);
		StructuralType structuralType = symbol.Category?.BuiltInCategory switch
		{
			BuiltInCategory.OST_StructuralColumns => StructuralType.Column,
			BuiltInCategory.OST_StructuralFraming => StructuralType.Beam,
			BuiltInCategory.OST_StructuralFoundation => StructuralType.Footing,
			_ => StructuralType.NonStructural
		};

		object result = RevitUtil.InTransaction(doc, "Colocar familia", () =>
		{
			if (!symbol.IsActive)
			{
				symbol.Activate();
				doc.Regenerate();
			}

			XYZ location = new(point.X, point.Y, point.Z + level.Elevation);
			FamilyInstance instance = hostId != null
				? doc.Create.NewFamilyInstance(location, symbol, RevitUtil.Get(doc, hostId), level, structuralType)
				: doc.Create.NewFamilyInstance(location, symbol, level, structuralType);

			if (Math.Abs(rotation) > 1e-9)
			{
				ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(location, location + XYZ.BasisZ), rotation);
			}

			return new { id = instance.Id.Value, family = symbol.FamilyName, type = symbol.Name, level = level.Name };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	public static object CreateTextNote(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		ElementId viewId = Args.Id(args, "viewId") ?? app.ActiveUIDocument.ActiveView.Id;
		string text = Args.RequiredStr(args, "text");
		XYZ point = Args.Point(args, "point");
		ElementId typeId = Args.Str(args, "textType") is { Length: > 0 } typeName
			? RevitUtil.FindType<TextNoteType>(doc, typeName, ElementTypeGroup.TextNoteType).Id
			: doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

		object result = RevitUtil.InTransaction(doc, "Crear texto",
			() => new { id = TextNote.Create(doc, viewId, point, text, typeId).Id.Value }, out List<string> warnings);
		return new { created = result, warnings };
	}

	public static object CreateSheet(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		string titleBlock = Args.Str(args, "titleBlock");
		ElementId titleBlockId = ElementId.InvalidElementId;
		if (!string.Equals(titleBlock, "none", StringComparison.OrdinalIgnoreCase))
		{
			titleBlockId = string.IsNullOrWhiteSpace(titleBlock)
				? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstElementId()
				: RevitUtil.FindSymbol(doc, titleBlock, "OST_TitleBlocks").Id;
		}

		object result = RevitUtil.InTransaction(doc, "Crear plano", () =>
		{
			ViewSheet sheet = ViewSheet.Create(doc, titleBlockId ?? ElementId.InvalidElementId);
			if (Args.Str(args, "number") is { Length: > 0 } number)
			{
				sheet.SheetNumber = number;
			}

			if (Args.Str(args, "name") is { Length: > 0 } name)
			{
				sheet.Name = name;
			}

			return new { id = sheet.Id.Value, number = sheet.SheetNumber, name = sheet.Name };
		}, out List<string> warnings);

		return new { created = result, warnings };
	}

	public static object PlaceViewOnSheet(UIApplication app, JsonObject args)
	{
		Document doc = RevitUtil.Doc(app);
		ElementId sheetId = Args.RequiredId(args, "sheetId");
		ElementId viewId = Args.RequiredId(args, "viewId");
		if (RevitUtil.Get(doc, sheetId) is not ViewSheet sheet)
		{
			throw new ArgumentException("sheetId no es un plano.");
		}

		if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
		{
			throw new ArgumentException("Esa vista no se puede colocar en el plano (¿ya está en otro plano?).");
		}

		XYZ point = Args.Point(args, "point", false);
		if (point == null)
		{
			BoundingBoxUV outline = sheet.Outline;
			point = new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
		}

		object result = RevitUtil.InTransaction(doc, "Colocar vista en plano",
			() => new { viewportId = Viewport.Create(doc, sheetId, viewId, point).Id.Value }, out List<string> warnings);
		return new { created = result, warnings };
	}

	private static XYZ Flat(XYZ p, double z = 0) => new(p.X, p.Y, z);
}
