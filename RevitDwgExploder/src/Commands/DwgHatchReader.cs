using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Tables;
using CadColor = ACadSharp.Color;
using CadXYZ = CSMath.XYZ;
using Matrix4 = CSMath.Matrix4;
using Transform = Autodesk.Revit.DB.Transform;
using XYZ = Autodesk.Revit.DB.XYZ;

namespace RevitDwgExploder.Commands;

/// <summary>Una familia de líneas paralelas del patrón de un hatch, ya en pies de modelo.</summary>
internal sealed class HatchGrid
{
	public double Angle;

	public double OriginX;

	public double OriginY;

	/// <summary>Distancia perpendicular entre líneas.</summary>
	public double Offset;

	/// <summary>Desplazamiento a lo largo de la línea entre una línea y la siguiente.</summary>
	public double Shift;

	/// <summary>Trazos y espacios alternados (positivos). Vacío = línea continua.</summary>
	public List<double> Segments = new List<double>();
}

/// <summary>Hatch del DWG listo para crear un Filled Region: contornos y patrón en coordenadas de Revit.</summary>
internal sealed class HatchRegion
{
	public string Layer;

	public List<List<XYZ>> Loops = new List<List<XYZ>>();

	public bool IsSolid;

	public string PatternName;

	public List<HatchGrid> Grids = new List<HatchGrid>();

	public byte R;

	public byte G;

	public byte B;
}

/// <summary>
/// Lee los HATCH del espacio modelo (y de bloques anidados) de un DWG y los convierte a coordenadas de
/// Revit con la misma transformación que el resto del CAD.
/// </summary>
internal static class DwgHatchReader
{
	private const int MaxBlockDepth = 8;

	private const int ArcPrecision = 48;

	public static List<HatchRegion> Read(CadDocument cad, double feetPerUnit, Transform instanceTransform)
	{
		var result = new List<HatchRegion>();
		if (cad?.Entities == null)
		{
			return result;
		}

		try
		{
			Collect(cad.Entities, Matrix4.Identity, null, feetPerUnit, instanceTransform, result, 0);
		}
		catch (Exception)
		{
		}

		return result;
	}

	private static void Collect(IEnumerable<Entity> entities, Matrix4 transform, CadColor? byBlockColor, double toFeet,
		Transform instanceTransform, List<HatchRegion> output, int depth)
	{
		foreach (Entity entity in entities)
		{
			if (entity == null || entity.IsInvisible || entity.Layer?.IsOn == false)
			{
				continue;
			}

			if (entity is Insert insert)
			{
				if (depth >= MaxBlockDepth || insert.Block == null)
				{
					continue;
				}

				Matrix4 child;
				try
				{
					child = transform * insert.GetTransform().Matrix;
				}
				catch (Exception)
				{
					continue;
				}

				Collect(insert.Block.Entities, child, ResolveColor(entity, byBlockColor), toFeet, instanceTransform, output, depth + 1);
				continue;
			}

			if (!(entity is Hatch hatch))
			{
				continue;
			}

			HatchRegion region = Convert(hatch, transform, byBlockColor, toFeet, instanceTransform);
			if (region != null && region.Loops.Count > 0)
			{
				output.Add(region);
			}
		}
	}

	private static HatchRegion Convert(Hatch hatch, Matrix4 transform, CadColor? byBlockColor, double toFeet, Transform instanceTransform)
	{
		var region = new HatchRegion
		{
			Layer = hatch.Layer?.Name ?? "0",
			IsSolid = hatch.IsSolid || hatch.Pattern == null || hatch.Pattern.Lines == null || hatch.Pattern.Lines.Count == 0,
			PatternName = hatch.Pattern?.Name ?? "SOLID"
		};

		CadColor color = ResolveColor(hatch, byBlockColor);
		SetRgb(region, color);

		XYZ ToWorld(CadXYZ local)
		{
			CadXYZ w = transform * local;
			return instanceTransform.OfPoint(new XYZ(w.X * toFeet, w.Y * toFeet, w.Z * toFeet));
		}

		foreach (Hatch.BoundaryPath path in hatch.Paths)
		{
			List<CadXYZ> local = PathToPoints(path, hatch.Elevation);
			if (local.Count < 3)
			{
				continue;
			}

			region.Loops.Add(local.Select(ToWorld).ToList());
		}

		if (!region.IsSolid)
		{
			// Rotación y escala totales (bloque + instancia) para llevar las líneas del patrón a Revit.
			XYZ o = ToWorld(new CadXYZ(0, 0, hatch.Elevation));
			XYZ ex = ToWorld(new CadXYZ(1, 0, hatch.Elevation)) - o;
			double rotation = Math.Atan2(ex.Y, ex.X);
			double scale = Math.Sqrt(ex.X * ex.X + ex.Y * ex.Y);
			foreach (HatchPattern.Line line in hatch.Pattern.Lines)
			{
				HatchGrid grid = ConvertLine(line, rotation, scale, ToWorld, hatch.Elevation);
				if (grid != null)
				{
					region.Grids.Add(grid);
				}
			}

			if (region.Grids.Count == 0)
			{
				region.IsSolid = true;
			}
		}

		return region;
	}

	private static HatchGrid ConvertLine(HatchPattern.Line line, double rotation, double scale, Func<CadXYZ, XYZ> toWorld, double elevation)
	{
		double angle = line.Angle + rotation;
		double cos = Math.Cos(line.Angle);
		double sin = Math.Sin(line.Angle);

		// El offset del DWG es un vector (ya rotado/escalado por el hatch): se descompone en la
		// dirección de la línea (shift) y su perpendicular (offset), que es lo que usa Revit.
		double shift = (line.Offset.X * cos + line.Offset.Y * sin) * scale;
		double offset = (-line.Offset.X * sin + line.Offset.Y * cos) * scale;
		if (Math.Abs(offset) < 1E-09)
		{
			return null;
		}

		if (offset < 0.0)
		{
			offset = -offset;
			shift = -shift;
		}

		XYZ origin = toWorld(new CadXYZ(line.BasePoint.X, line.BasePoint.Y, elevation));
		var grid = new HatchGrid
		{
			Angle = NormalizeAngle(angle),
			OriginX = origin.X,
			OriginY = origin.Y,
			Offset = offset,
			Shift = shift
		};

		List<double> dashes = line.DashLengths ?? new List<double>();
		if (dashes.Count > 0 && dashes.Any(d => d < 0.0))
		{
			grid.Segments = NormalizeDashes(dashes.Select(d => d * scale).ToList(), offset);
		}

		return grid;
	}

	/// <summary>Trazo/espacio alternados, empezando por trazo, número par y todos positivos (formato de Revit).</summary>
	private static List<double> NormalizeDashes(List<double> dashes, double offset)
	{
		double dot = Math.Max(offset * 0.02, 1E-04);
		var merged = new List<(bool Mark, double Length)>();
		foreach (double d in dashes)
		{
			bool mark = d >= 0.0;
			double len = d == 0.0 ? dot : Math.Abs(d);
			if (merged.Count > 0 && merged[merged.Count - 1].Mark == mark)
			{
				merged[merged.Count - 1] = (mark, merged[merged.Count - 1].Length + len);
			}
			else
			{
				merged.Add((mark, len));
			}
		}

		int firstMark = merged.FindIndex(m => m.Mark);
		if (firstMark < 0 || !merged.Any(m => !m.Mark))
		{
			return new List<double>();
		}

		merged = merged.Skip(firstMark).Concat(merged.Take(firstMark)).ToList();
		if (merged.Count > 1 && merged[merged.Count - 1].Mark)
		{
			merged[0] = (true, merged[0].Length + merged[merged.Count - 1].Length);
			merged.RemoveAt(merged.Count - 1);
		}

		return merged.Count % 2 == 0 ? merged.Select(m => Math.Max(m.Length, 1E-04)).ToList() : new List<double>();
	}

	private static List<CadXYZ> PathToPoints(Hatch.BoundaryPath path, double elevation)
	{
		var points = new List<CadXYZ>();
		foreach (Hatch.BoundaryPath.Edge edge in path.Edges)
		{
			List<CadXYZ> edgePoints = EdgeToPoints(edge, elevation);
			if (edgePoints == null || edgePoints.Count == 0)
			{
				continue;
			}

			if (points.Count > 0)
			{
				// Los bordes pueden venir en cualquier sentido: se orientan para encadenarse.
				CadXYZ last = points[points.Count - 1];
				if (Dist(last, edgePoints[edgePoints.Count - 1]) < Dist(last, edgePoints[0]))
				{
					edgePoints.Reverse();
				}

				if (Dist(last, edgePoints[0]) < 1E-09)
				{
					edgePoints.RemoveAt(0);
				}
			}

			points.AddRange(edgePoints);
		}

		if (points.Count > 1 && Dist(points[0], points[points.Count - 1]) < 1E-09)
		{
			points.RemoveAt(points.Count - 1);
		}

		return points;
	}

	private static List<CadXYZ> EdgeToPoints(Hatch.BoundaryPath.Edge edge, double elevation)
	{
		try
		{
			switch (edge)
			{
				case Hatch.BoundaryPath.Line line:
					return new List<CadXYZ> { new CadXYZ(line.Start.X, line.Start.Y, elevation), new CadXYZ(line.End.X, line.End.Y, elevation) };
				case Hatch.BoundaryPath.Polyline polyline:
				{
					var entity = polyline.ToEntity() as IPolyline;
					if (entity == null)
					{
						return null;
					}

					List<CadXYZ> pts = entity.GetPoints<CadXYZ>(ArcPrecision).Select(p => new CadXYZ(p.X, p.Y, elevation)).ToList();
					return pts;
				}
				default:
				{
					Entity entity = edge.ToEntity();
					List<CadXYZ> pts = entity switch
					{
						ACadSharp.Entities.Arc arc => arc.PolygonalVertexes(ArcPrecision),
						ACadSharp.Entities.Circle circle => circle.PolygonalVertexes(ArcPrecision),
						ACadSharp.Entities.Ellipse ellipse => ellipse.PolygonalVertexes(ArcPrecision),
						ACadSharp.Entities.Spline spline => spline.TryPolygonalVertexes(ArcPrecision * 2, out List<CadXYZ> sp) ? sp : null,
						_ => null
					};
					return pts?.Select(p => new CadXYZ(p.X, p.Y, elevation)).ToList();
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static double Dist(CadXYZ a, CadXYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

	private static double NormalizeAngle(double a)
	{
		const double twoPi = 2.0 * Math.PI;
		a %= twoPi;
		return a < 0.0 ? a + twoPi : a;
	}

	private static CadColor ResolveColor(Entity entity, CadColor? byBlockColor)
	{
		CadColor color = entity.Color;
		if (color.IsByLayer)
		{
			return entity.Layer?.Color ?? new CadColor(7);
		}

		if (color.IsByBlock)
		{
			return byBlockColor ?? entity.Layer?.Color ?? new CadColor(7);
		}

		return color;
	}

	private static void SetRgb(HatchRegion region, CadColor color)
	{
		try
		{
			// ACI 7 (blanco/negro según el fondo) se dibuja negro en papel.
			if (!color.IsTrueColor && (color.Index == 7 || color.Index <= 0 || color.Index > 255))
			{
				region.R = region.G = region.B = 0;
				return;
			}

			region.R = color.R;
			region.G = color.G;
			region.B = color.B;
			if (region.R > 250 && region.G > 250 && region.B > 250)
			{
				region.R = region.G = region.B = 0;
			}
		}
		catch (Exception)
		{
			region.R = region.G = region.B = 0;
		}
	}
}
