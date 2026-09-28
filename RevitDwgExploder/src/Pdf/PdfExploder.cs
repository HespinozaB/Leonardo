using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgExploder.Commands;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Pdf;

/// <summary>Resultados acumulados de una o varias explotaciones de PDF.</summary>
internal sealed class PdfExplodeStats
{
	public int Pages;
	public int Lines;
	public int Arcs;
	public int Splines;
	public int Skipped;
	public int Fills;
	public int FailedFills;
	public int Texts;
	public int Images;
	public int CreatedStyles;
	public int CreatedViews;
	public List<string> Failed = new List<string>();
	public int SuppressedDialogs;
	public int DeletedOriginals;
}

/// <summary>
/// Convierte el contenido de una página PDF en elementos nativos de una vista: Filled Regions (rellenos),
/// Detail Lines (trazos, con Line Styles por color/grosor/patrón) y TextNotes (textos).
/// </summary>
internal static class PdfExploder
{
	/// <summary>Un punto PDF (1/72") en pies.</summary>
	public const double FeetPerPoint = 1.0 / 864.0;

	/// <summary>
	/// Relación altura de mayúsculas / tamaño de letra (em) en Arial: el tamaño del PDF es el "em" y los tipos
	/// de texto (igual que en los DWG) se calculan a partir de la altura de las mayúsculas.
	/// </summary>
	private const double CapHeightRatio = 0.72;

	private const int CurveBatchSize = 500;

	/// <summary>
	/// Explota una página en <paramref name="view"/>. <paramref name="origin"/> es el punto del modelo que
	/// corresponde a la esquina inferior izquierda de la página y <paramref name="feetPerPt"/> la escala.
	/// </summary>
	public static bool Explode(Document doc, View view, PdfDrawing drawing, XYZ origin, double feetPerPt, double shortCurveTolerance, PdfExplodeStats stats)
	{
		XYZ right = view.RightDirection;
		XYZ up = view.UpDirection;
		XYZ ToModel(Pt p) => origin + right.Multiply(p.X * feetPerPt) + up.Multiply(p.Y * feetPerPt);

		double minLength = shortCurveTolerance * 1.01;
		using var tx = new Transaction(doc, "EMASY: explotar PDF");
		FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
		failureOptions.SetFailuresPreprocessor(new ExplodeDwgCommand.WarningSwallower());
		failureOptions.SetClearAfterRollback(true);
		tx.SetFailureHandlingOptions(failureOptions);
		tx.Start();

		// 1) Rellenos (debajo de todo, en el orden del PDF).
		var regions = new FilledRegionBuilder(doc, view, shortCurveTolerance);
		foreach (PdfFillShape fill in drawing.Fills)
		{
			var region = new HatchRegion
			{
				Layer = "PDF",
				IsSolid = true,
				PatternName = "SOLID",
				R = fill.R,
				G = fill.G,
				B = fill.B,
				Loops = fill.Loops.Select(loop => loop.Select(ToModel).ToList()).ToList()
			};
			if (regions.Create(region))
			{
				stats.Fills++;
			}
			else
			{
				stats.FailedFills++;
			}
		}

		// 2) Trazos → Detail Lines agrupadas por Line Style.
		var styles = new PdfLineStyles(doc);
		var byStyle = new Dictionary<ElementId, List<Curve>>();
		var seen = new HashSet<(long, long, long, long)>();
		foreach (PdfStroke stroke in drawing.Strokes)
		{
			GraphicsStyle style = styles.Get(stroke.Style);
			ElementId key = style?.Id ?? ElementId.InvalidElementId;
			if (!byStyle.TryGetValue(key, out List<Curve> list))
			{
				list = new List<Curve>();
				byStyle[key] = list;
			}

			foreach (PdfSegment segment in stroke.Segments)
			{
				foreach (Curve curve in ToCurves(segment, ToModel, minLength, stats))
				{
					if (curve is Line && !seen.Add(LineKey(curve)))
					{
						continue;
					}

					list.Add(curve);
				}
			}
		}

		foreach (var pair in byStyle)
		{
			GraphicsStyle style = pair.Key == ElementId.InvalidElementId ? null : doc.GetElement(pair.Key) as GraphicsStyle;
			CreateCurves(doc, view, pair.Value, style, stats);
		}

		// 3) Textos (encima de todo).
		var textTypes = new ExplodeDwgCommand.TextNoteTypeCache(doc, view.Scale, ExplodeDwgCommand.MinTextSizeFeet);
		foreach (PdfTextItem text in drawing.Texts)
		{
			var entry = new DwgTextImporter.DwgTextEntry
			{
				Text = text.Text,
				Position = ToModel(text.Origin),
				HeightFeet = text.SizePt * CapHeightRatio * feetPerPt,
				RotationRadians = text.Rotation,
				AnchorH = TextAnchorH.Left,
				AnchorV = TextAnchorV.Bottom,
				WidthFactor = 1.0,
				FontName = text.FontName,
				Bold = text.Bold
			};
			if (ExplodeDwgCommand.CreateTextNote(doc, view, entry, textTypes))
			{
				stats.Texts++;
			}
		}

		stats.Images += drawing.Images;
		stats.CreatedStyles += styles.Created;
		stats.Pages++;
		return tx.Commit() == TransactionStatus.Committed;
	}

	// ------------------------------------------------------------------ Curvas

	private static IEnumerable<Curve> ToCurves(PdfSegment segment, Func<Pt, XYZ> toModel, double minLength, PdfExplodeStats stats)
	{
		XYZ a = toModel(segment.Start);
		XYZ b = toModel(segment.End);
		if (!segment.IsBezier)
		{
			if (a.DistanceTo(b) <= minLength)
			{
				stats.Skipped++;
				yield break;
			}

			stats.Lines++;
			yield return Line.CreateBound(a, b);
			yield break;
		}

		XYZ c1 = toModel(segment.Control1);
		XYZ c2 = toModel(segment.Control2);
		XYZ Bezier(double t)
		{
			double u = 1.0 - t;
			return a * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + b * (t * t * t);
		}

		// ¿Es un arco de circunferencia? (los círculos del PDF son 4 Béziers): se crea un Arc real.
		Curve arc = TryArc(a, b, Bezier(0.5), Bezier(0.25), Bezier(0.75), minLength);
		if (arc != null)
		{
			stats.Arcs++;
			yield return arc;
			yield break;
		}

		Curve spline = null;
		try
		{
			if (a.DistanceTo(b) > minLength || a.DistanceTo(c1) > minLength)
			{
				spline = NurbSpline.CreateCurve(3, new List<double> { 0, 0, 0, 0, 1, 1, 1, 1 }, new List<XYZ> { a, c1, c2, b });
			}
		}
		catch (Exception)
		{
			spline = null;
		}

		if (spline != null && spline.Length > minLength)
		{
			stats.Splines++;
			yield return spline;
			yield break;
		}

		// Último recurso: la curva como polilínea.
		XYZ previous = a;
		for (int i = 1; i <= 8; i++)
		{
			XYZ next = Bezier(i / 8.0);
			if (previous.DistanceTo(next) > minLength)
			{
				stats.Lines++;
				yield return Line.CreateBound(previous, next);
				previous = next;
			}
		}
	}

	private static Curve TryArc(XYZ start, XYZ end, XYZ mid, XYZ q1, XYZ q3, double minLength)
	{
		try
		{
			if (start.DistanceTo(end) <= minLength || start.DistanceTo(mid) <= minLength || mid.DistanceTo(end) <= minLength)
			{
				return null;
			}

			Arc arc = Arc.Create(start, end, mid);
			double tolerance = Math.Max(arc.Radius * 0.002, minLength * 0.5);
			if (Math.Abs(q1.DistanceTo(arc.Center) - arc.Radius) <= tolerance && Math.Abs(q3.DistanceTo(arc.Center) - arc.Radius) <= tolerance)
			{
				return arc;
			}
		}
		catch (Exception)
		{
		}

		return null;
	}

	private static (long, long, long, long) LineKey(Curve line)
	{
		XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
		long ax = (long)Math.Round(a.X * 1E5), ay = (long)Math.Round(a.Y * 1E5);
		long bx = (long)Math.Round(b.X * 1E5), by = (long)Math.Round(b.Y * 1E5);
		return ax < bx || (ax == bx && ay <= by) ? (ax, ay, bx, by) : (bx, by, ax, ay);
	}

	private static void CreateCurves(Document doc, View view, List<Curve> curves, GraphicsStyle style, PdfExplodeStats stats)
	{
		for (int start = 0; start < curves.Count; start += CurveBatchSize)
		{
			List<Curve> batch = curves.GetRange(start, Math.Min(CurveBatchSize, curves.Count - start));
			var created = new List<DetailCurve>();
			try
			{
				var array = new CurveArray();
				foreach (Curve c in batch)
				{
					array.Append(c);
				}

				foreach (DetailCurve dc in doc.Create.NewDetailCurveArray(view, array))
				{
					created.Add(dc);
				}
			}
			catch (Exception)
			{
				// Alguna curva del lote no es válida: se crean una a una.
				foreach (Curve c in batch)
				{
					try
					{
						created.Add(doc.Create.NewDetailCurve(view, c));
					}
					catch (Exception)
					{
						stats.Skipped++;
					}
				}
			}

			if (style == null)
			{
				continue;
			}

			foreach (DetailCurve dc in created)
			{
				try
				{
					dc.LineStyle = style;
				}
				catch (Exception)
				{
				}
			}
		}
	}

	// ------------------------------------------------------------------ Colocación

	/// <summary>Página del PDF que muestra una imagen de Revit (1 si no se indica).</summary>
	public static int PageOf(ImageType type)
	{
		try
		{
			return Math.Max(1, type.PageNumber);
		}
		catch (Exception)
		{
			return 1;
		}
	}

	public static bool IsPdf(ImageType type)
	{
		try
		{
			return type != null && string.Equals(Path.GetExtension(type.Path ?? string.Empty), ".pdf", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Explota un PDF insertado en Revit en su lugar (misma posición y tamaño que la imagen).</summary>
	public static bool ExplodeImage(Document doc, ImageInstance instance, string pdfPath, double shortCurveTolerance, PdfExplodeStats stats)
	{
		var type = doc.GetElement(instance.GetTypeId()) as ImageType;
		var view = doc.GetElement(instance.OwnerViewId) as View;
		if (type == null || view == null)
		{
			return false;
		}

		PdfDrawing drawing = PdfPageReader.Read(pdfPath, PageOf(type));
		if (drawing.Width <= 0.0 || instance.Width <= 0.0)
		{
			return false;
		}

		XYZ origin = instance.GetLocation(BoxPlacement.BottomLeft);
		double feetPerPt = instance.Width / drawing.Width;
		return Explode(doc, view, drawing, origin, feetPerPt, shortCurveTolerance, stats);
	}

	/// <summary>
	/// Busca en los textos de la primera página una escala tipo "ESC 1:50", "ESCALA 1/100" o "SCALE 1:20".
	/// Devuelve 0 si no encuentra ninguna.
	/// </summary>
	public static int DetectScale(PdfDrawing drawing)
	{
		var regex = new System.Text.RegularExpressions.Regex(@"(?:ESC|ESCALA|SCALE)\.?\s*:?\s*1\s*[:/]\s*(\d{1,5})",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		return drawing.Texts
			.Select(t => regex.Match(t.Text))
			.Where(m => m.Success)
			.Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
			.Where(v => v > 0)
			.GroupBy(v => v)
			.OrderByDescending(g => g.Count())
			.Select(g => g.Key)
			.FirstOrDefault();
	}
}

/// <summary>Line Styles "PDF R-G-B 0.25mm [discontinua]" creados una vez por estilo de trazo del PDF.</summary>
internal sealed class PdfLineStyles
{
	/// <summary>Grosores (mm) a partir de los cuales se usa cada número de pluma de Revit.</summary>
	private static readonly double[] PenThresholds = { 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0, 2.8, 4.0, 5.0, 6.0, 7.0, 8.0 };

	private readonly Document _doc;

	private readonly Category _lines;

	private readonly Dictionary<string, Category> _existing;

	private readonly Dictionary<PdfStrokeStyle, GraphicsStyle> _cache = new Dictionary<PdfStrokeStyle, GraphicsStyle>();

	public int Created { get; private set; }

	public PdfLineStyles(Document doc)
	{
		_doc = doc;
		_lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
		_existing = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
		if (_lines != null)
		{
			foreach (Category sub in _lines.SubCategories)
			{
				_existing[sub.Name] = sub;
			}
		}
	}

	public GraphicsStyle Get(PdfStrokeStyle style)
	{
		if (_cache.TryGetValue(style, out GraphicsStyle cached))
		{
			return cached;
		}

		GraphicsStyle result = null;
		try
		{
			double widthMm = Math.Max(0.0, style.WidthPt) * 25.4 / 72.0;
			string name = $"PDF {style.R:D3}-{style.G:D3}-{style.B:D3} {widthMm.ToString("0.00", CultureInfo.InvariantCulture)}mm"
				+ (style.Dash.Length > 0 ? " [" + style.Dash + "]" : string.Empty);
			name = name.Replace("[", "(").Replace("]", ")");
			if (!_existing.TryGetValue(name, out Category sub) && _lines != null)
			{
				sub = _doc.Settings.Categories.NewSubcategory(_lines, name);
				sub.LineColor = new Color(style.R, style.G, style.B);
				int pen = 1 + PenThresholds.Count(t => widthMm > t);
				sub.SetLineWeight(Math.Min(16, pen), GraphicsStyleType.Projection);
				ElementId pattern = DashPattern(style.Dash);
				if (pattern != null)
				{
					sub.SetLinePatternId(pattern, GraphicsStyleType.Projection);
				}

				_existing[name] = sub;
				Created++;
			}

			result = sub?.GetGraphicsStyle(GraphicsStyleType.Projection);
		}
		catch (Exception)
		{
			result = null;
		}

		_cache[style] = result;
		return result;
	}

	/// <summary>Patrón de línea a partir del patrón de trazos del PDF (en puntos de papel).</summary>
	private ElementId DashPattern(string dash)
	{
		if (string.IsNullOrEmpty(dash))
		{
			return null;
		}

		List<double> values = dash.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList();
		if (values.Count % 2 == 1)
		{
			values.AddRange(values.ToList());
		}

		string name = "PDF-trazos " + dash;
		LinePatternElement existing = LinePatternElement.GetLinePatternElementByName(_doc, name);
		if (existing != null)
		{
			return existing.Id;
		}

		var segments = new List<LinePatternSegment>();
		for (int i = 0; i < values.Count; i++)
		{
			double feet = Math.Max(values[i] * PdfExploder.FeetPerPoint, 0.15 / 304.8);
			bool mark = i % 2 == 0;
			segments.Add(mark
				? (values[i] <= 0.01 ? new LinePatternSegment(LinePatternSegmentType.Dot, 0.0) : new LinePatternSegment(LinePatternSegmentType.Dash, feet))
				: new LinePatternSegment(LinePatternSegmentType.Space, feet));
		}

		try
		{
			var pattern = new LinePattern(name);
			pattern.SetSegments(segments);
			return LinePatternElement.Create(_doc, pattern).Id;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
