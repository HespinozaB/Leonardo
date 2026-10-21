using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgExploder.Commands;

namespace RevitParamFill.Core;

/// <summary>
/// Lee los sectores de un conjunto: regiones rellenas o lazos de líneas de detalle de una planta, o polilíneas cerradas de una capa
/// de un DWG. El nombre de cada sector es el texto que cae dentro (TextNote o texto del DWG), el tipo/capa o el comentario.
/// </summary>
public static class SectorReader
{
	/// <summary>Tolerancia para unir extremos de líneas en un lazo (pies, ≈ 3 mm).</summary>
	private const double JoinTolerance = 0.01;

	public static string Read(Document doc, SectorSet set)
	{
		set.Sectors = new List<Polygon>();
		if (!(doc.GetElement(new ElementId(set.ViewId)) is View view))
		{
			return $"{set.Name}: elige la vista de planta.";
		}

		return set.Source == SectorSource.Trazo ? ReadSketch(doc, view, set) : ReadDwg(doc, view, set);
	}

	private static string ReadSketch(Document doc, View view, SectorSet set)
	{
		var texts = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
			.Select(t => (Text: FirstLine(t.Text), t.Coord.X, t.Coord.Y)).Where(t => t.Text.Length > 0).ToList();
		var raw = new List<(List<(double, double)> Points, string TypeName, string Comment, long Id)>();
		var reader = new ModelReader(doc);
		if (set.UseFilledRegions)
		{
			foreach (FilledRegion region in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>())
			{
				string typeName = doc.GetElement(region.GetTypeId())?.Name ?? string.Empty;
				if (set.Filter.Length > 0 && typeName != set.Filter)
				{
					continue;
				}

				// El contorno exterior es el lazo de mayor área (los demás son huecos).
				List<(double, double)> outer = region.GetBoundaries().Select(Tessellate).Where(p => p.Count >= 3)
					.OrderByDescending(p => new Polygon(string.Empty, p).Area).FirstOrDefault();
				if (outer != null)
				{
					// "Comment" lleva el valor del parámetro elegido para nombrar (Comentarios, Marca u otro).
					string label = set.NameFrom == SectorName.Parametro && set.NameParameter.Length > 0
						? set.NameParameter == SectorSet.MarkToken
							? reader.Value(region.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)) ?? string.Empty
							: reader.Value(region.LookupParameter(set.NameParameter)) ?? reader.Value(reader.TypeOf(region)?.LookupParameter(set.NameParameter)) ?? string.Empty
						: region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
					raw.Add((outer, typeName, label, region.Id.Value));
				}
			}
		}
		else
		{
			var segments = new List<(List<(double, double)> Points, long Id)>();
			foreach (CurveElement line in new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>())
			{
				if (line.CurveElementType != CurveElementType.DetailCurve || line.GeometryCurve == null)
				{
					continue;
				}

				string style = (line.LineStyle as GraphicsStyle)?.Name ?? string.Empty;
				if (set.Filter.Length > 0 && style != set.Filter)
				{
					continue;
				}

				segments.Add((line.GeometryCurve.Tessellate().Select(p => (p.X, p.Y)).ToList(), line.Id.Value));
			}

			foreach (var loop in Loops(segments))
			{
				raw.Add((loop.Points, set.Filter, string.Empty, loop.Id));
			}
		}

		Name(set, raw, texts);
		int distinct = set.Sectors.Select(p => p.Name).Distinct().Count();
		return $"{set.Name}: {set.Sectors.Count} región(es) leídas de {(set.UseFilledRegions ? "regiones rellenas" : "líneas de detalle")} en \"{view.Name}\", " +
			$"{distinct} sector(es) distintos" + (distinct < set.Sectors.Count ? " (las regiones con el mismo nombre forman un solo sector)." : ".");
	}

	private static string ReadDwg(Document doc, View view, SectorSet set)
	{
		if (!(doc.GetElement(new ElementId(set.DwgInstanceId)) is ImportInstance instance))
		{
			return $"{set.Name}: elige el DWG.";
		}

		var segments = new List<(List<(double, double)> Points, long Id)>();
		var closed = new List<List<(double, double)>>();
		foreach (var (geometry, layer) in DwgGeometry(doc, instance, view))
		{
			if (set.Layer.Length > 0 && layer != set.Layer)
			{
				continue;
			}

			if (geometry is PolyLine polyline)
			{
				List<(double, double)> points = polyline.GetCoordinates().Select(p => (p.X, p.Y)).ToList();
				if (points.Count >= 4 && Near(points[0], points[points.Count - 1]))
				{
					closed.Add(points.Take(points.Count - 1).ToList());
				}
				else
				{
					segments.Add((points, instance.Id.Value));
				}
			}
			else if (geometry is Curve curve)
			{
				segments.Add((curve.Tessellate().Select(p => (p.X, p.Y)).ToList(), instance.Id.Value));
			}
		}

		closed.AddRange(Loops(segments).Select(l => l.Points));

		var texts = new List<(string Text, double X, double Y)>();
		string textNote = string.Empty;
		if (set.NameFrom == SectorName.Texto)
		{
			var status = DwgTextImporter.TryReadTexts(doc, instance, new DwgTextImporter.FileCache(), out List<DwgTextImporter.DwgTextEntry> entries,
				string.IsNullOrWhiteSpace(set.DwgPath) ? null : set.DwgPath);
			if (status == DwgTextImporter.ReadStatus.Ok)
			{
				texts = entries.Select(t => (FirstLine(t.Text), t.Position.X, t.Position.Y)).Where(t => t.Item1.Length > 0).ToList();
			}
			else
			{
				textNote = status == DwgTextImporter.ReadStatus.NotLinked
					? " El DWG está importado: indica su archivo para leer los textos."
					: " No se pudieron leer los textos del DWG (" + status + ").";
			}
		}

		Name(set, closed.Select(p => (p, set.Layer, string.Empty, instance.Id.Value)).ToList(), texts);
		return $"{set.Name}: {set.Sectors.Count} sector(es) leídos del DWG (capa {(set.Layer.Length > 0 ? set.Layer : "todas")}).{textNote}";
	}

	/// <summary>Las capas del DWG con geometría (para elegir la de los sectores).</summary>
	public static List<string> DwgLayers(Document doc, ImportInstance instance, View view) =>
		DwgGeometry(doc, instance, view).Select(g => g.Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase).ToList();

	private static IEnumerable<(GeometryObject Geometry, string Layer)> DwgGeometry(Document doc, ImportInstance instance, View view)
	{
		var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
		if (view != null && !(view is View3D))
		{
			options.View = view;
		}

		GeometryElement root;
		try
		{
			root = instance.get_Geometry(options);
		}
		catch (Exception)
		{
			root = instance.get_Geometry(new Options());
		}

		if (root == null)
		{
			yield break;
		}

		var layers = new Dictionary<long, string>();
		foreach (GeometryObject top in root)
		{
			if (!(top is GeometryInstance geometryInstance))
			{
				continue;
			}

			foreach (GeometryObject geometry in geometryInstance.GetInstanceGeometry())
			{
				long styleId = geometry.GraphicsStyleId.Value;
				if (!layers.TryGetValue(styleId, out string layer))
				{
					layer = (doc.GetElement(geometry.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? string.Empty;
					layers[styleId] = layer;
				}

				yield return (geometry, layer);
			}
		}
	}

	/// <summary>Pone nombre a cada polígono: el texto de dentro más cercano a su centro, o el tipo/capa, o el comentario.</summary>
	private static void Name(SectorSet set, List<(List<(double, double)> Points, string TypeName, string Comment, long Id)> raw,
		List<(string Text, double X, double Y)> texts)
	{
		int index = 0;
		foreach (var (points, typeName, comment, id) in raw)
		{
			index++;
			var polygon = new Polygon(string.Empty, points, id);
			string name = null;
			if (set.NameFrom == SectorName.Texto)
			{
				double cx = (polygon.MinX + polygon.MaxX) / 2;
				double cy = (polygon.MinY + polygon.MaxY) / 2;
				name = texts.Where(t => polygon.Contains(t.X, t.Y))
					.OrderBy(t => Math.Pow(t.X - cx, 2) + Math.Pow(t.Y - cy, 2)).Select(t => t.Text).FirstOrDefault();
			}
			else if (set.NameFrom == SectorName.Comentarios || set.NameFrom == SectorName.Parametro)
			{
				name = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
			}
			else
			{
				name = string.IsNullOrWhiteSpace(typeName) ? null : typeName + (raw.Count(r => r.TypeName == typeName) > 1 ? " " + index : string.Empty);
			}

			polygon.Name = name ?? $"{set.Name} {index} (sin nombre)";
			set.Sectors.Add(polygon);
		}
	}

	private static List<(double, double)> Tessellate(CurveLoop loop)
	{
		var points = new List<(double, double)>();
		foreach (Curve curve in loop)
		{
			IList<XYZ> tessellated = curve.Tessellate();
			for (int i = 0; i < tessellated.Count - 1; i++)
			{
				points.Add((tessellated[i].X, tessellated[i].Y));
			}
		}

		return points;
	}

	/// <summary>Une segmentos sueltos por sus extremos y devuelve los lazos cerrados.</summary>
	private static List<(List<(double, double)> Points, long Id)> Loops(List<(List<(double, double)> Points, long Id)> segments)
	{
		var loops = new List<(List<(double, double)>, long)>();
		var unused = segments.Where(s => s.Points.Count >= 2).ToList();
		while (unused.Count > 0)
		{
			var first = unused[0];
			unused.RemoveAt(0);
			var chain = new List<(double, double)>(first.Points);
			bool extended = true;
			while (extended && !Near(chain[0], chain[chain.Count - 1]))
			{
				extended = false;
				for (int i = 0; i < unused.Count; i++)
				{
					List<(double, double)> points = unused[i].Points;
					if (Near(chain[chain.Count - 1], points[0]))
					{
						chain.AddRange(points.Skip(1));
					}
					else if (Near(chain[chain.Count - 1], points[points.Count - 1]))
					{
						chain.AddRange(Enumerable.Reverse(points).Skip(1));
					}
					else
					{
						continue;
					}

					unused.RemoveAt(i);
					extended = true;
					break;
				}
			}

			if (chain.Count >= 4 && Near(chain[0], chain[chain.Count - 1]))
			{
				loops.Add((chain.Take(chain.Count - 1).ToList(), first.Id));
			}
		}

		return loops;
	}

	private static bool Near((double X, double Y) a, (double X, double Y) b) =>
		Math.Abs(a.X - b.X) <= JoinTolerance && Math.Abs(a.Y - b.Y) <= JoinTolerance;

	private static string FirstLine(string text) =>
		(text ?? string.Empty).Replace("\r", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
}
