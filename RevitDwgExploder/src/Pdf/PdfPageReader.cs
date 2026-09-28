using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;

namespace RevitDwgExploder.Pdf;

/// <summary>Punto en coordenadas de la página tal como se ve (puntos PDF, origen abajo a la izquierda).</summary>
internal readonly struct Pt
{
	public readonly double X;

	public readonly double Y;

	public Pt(double x, double y)
	{
		X = x;
		Y = y;
	}
}

/// <summary>Tramo de un trazo: recta (P1 = P2 = null) o curva de Bézier cúbica.</summary>
internal sealed class PdfSegment
{
	public Pt Start;

	public Pt End;

	public bool IsBezier;

	public Pt Control1;

	public Pt Control2;
}

/// <summary>Estilo de trazo: color, grosor (puntos) y patrón de trazos (puntos, vacío = continuo).</summary>
internal readonly struct PdfStrokeStyle : IEquatable<PdfStrokeStyle>
{
	public readonly byte R;

	public readonly byte G;

	public readonly byte B;

	public readonly double WidthPt;

	public readonly string Dash;

	public PdfStrokeStyle(byte r, byte g, byte b, double widthPt, string dash)
	{
		R = r;
		G = g;
		B = b;
		WidthPt = Math.Round(widthPt, 2);
		Dash = dash ?? string.Empty;
	}

	public bool Equals(PdfStrokeStyle o) => R == o.R && G == o.G && B == o.B && WidthPt.Equals(o.WidthPt) && Dash == o.Dash;

	public override bool Equals(object obj) => obj is PdfStrokeStyle o && Equals(o);

	public override int GetHashCode() => (R, G, B, WidthPt, Dash).GetHashCode();
}

internal sealed class PdfStroke
{
	public PdfStrokeStyle Style;

	public List<PdfSegment> Segments = new List<PdfSegment>();
}

internal sealed class PdfFillShape
{
	public List<List<Pt>> Loops = new List<List<Pt>>();

	public byte R;

	public byte G;

	public byte B;
}

internal sealed class PdfTextItem
{
	public string Text;

	/// <summary>Inicio de la línea base del primer carácter.</summary>
	public Pt Origin;

	/// <summary>Tamaño de letra efectivo (puntos, tamaño "em").</summary>
	public double SizePt;

	public double Rotation;

	public string FontName;

	public bool Bold;
}

/// <summary>Contenido de una página PDF listo para convertir en elementos de Revit.</summary>
internal sealed class PdfDrawing
{
	/// <summary>Ancho y alto de la página tal como se ve (puntos, ya con la rotación de página).</summary>
	public double Width;

	public double Height;

	public List<PdfStroke> Strokes = new List<PdfStroke>();

	public List<PdfFillShape> Fills = new List<PdfFillShape>();

	public List<PdfTextItem> Texts = new List<PdfTextItem>();

	public int Images;
}

/// <summary>
/// Lee una página de un PDF con PdfPig: trazos (rectas y curvas), rellenos y textos, en coordenadas de la
/// página visible (recorte y rotación de página aplicados), en puntos (1/72").
/// </summary>
internal static class PdfPageReader
{
	private const int BezierSteps = 12;

	public static int CountPages(string path)
	{
		using PdfDocument document = PdfDocument.Open(path);
		return document.NumberOfPages;
	}

	public static PdfDrawing Read(string path, int pageNumber)
	{
		using PdfDocument document = PdfDocument.Open(path);
		pageNumber = Math.Max(1, Math.Min(document.NumberOfPages, pageNumber));
		Page page = document.GetPage(pageNumber);

		// PdfPig ya entrega las coordenadas en el espacio de la página tal como se ve: con el origen en la esquina
		// del recorte y la rotación de página aplicada. El tamaño visible sale del recorte rotado.
		PdfRectangle visible = page.CropBox.GetVisibleBounds(page.Rotation);
		Pt Map(PdfPoint p) => new Pt(p.X, p.Y);

		var drawing = new PdfDrawing
		{
			Width = Math.Abs(visible.Width),
			Height = Math.Abs(visible.Height)
		};

		ReadPaths(page, drawing, Map);
		ReadTexts(page, drawing, Map);
		try
		{
			drawing.Images = page.NumberOfImages;
		}
		catch (Exception)
		{
		}

		return drawing;
	}

	// ------------------------------------------------------------------ Trazos y rellenos

	private static void ReadPaths(Page page, PdfDrawing drawing, Func<PdfPoint, Pt> map)
	{
		double pageArea = drawing.Width * drawing.Height;
		foreach (PdfPath path in page.Paths)
		{
			if (path == null || path.IsClipping || path.Count == 0)
			{
				continue;
			}

			if (path.IsFilled)
			{
				var fill = new PdfFillShape();
				(fill.R, fill.G, fill.B) = ToRgb(path.FillColor);
				foreach (PdfSubpath subpath in path)
				{
					List<Pt> loop = Polygonize(subpath, map);
					if (loop.Count >= 3)
					{
						fill.Loops.Add(loop);
					}
				}

				// El fondo blanco de toda la página no es parte del dibujo.
				bool isPageBackground = fill.R > 250 && fill.G > 250 && fill.B > 250 && Area(fill.Loops) > pageArea * 0.8;
				if (fill.Loops.Count > 0 && !isPageBackground)
				{
					drawing.Fills.Add(fill);
				}
			}

			if (path.IsStroked)
			{
				(byte r, byte g, byte b) = ToRgb(path.StrokeColor);
				var stroke = new PdfStroke
				{
					Style = new PdfStrokeStyle(r, g, b, path.LineWidth, DashKey(path))
				};
				foreach (PdfSubpath subpath in path)
				{
					AddSegments(subpath, stroke.Segments, map);
				}

				if (stroke.Segments.Count > 0)
				{
					drawing.Strokes.Add(stroke);
				}
			}
		}
	}

	private static string DashKey(PdfPath path)
	{
		try
		{
			IReadOnlyList<double> array = path.LineDashPattern?.Array;
			if (array == null || array.Count == 0 || array.All(v => v <= 0.0))
			{
				return string.Empty;
			}

			return string.Join(",", array.Select(v => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static void AddSegments(PdfSubpath subpath, List<PdfSegment> output, Func<PdfPoint, Pt> map)
	{
		PdfPoint? start = null;
		PdfPoint? current = null;
		foreach (PdfSubpath.IPathCommand command in subpath.Commands)
		{
			switch (command)
			{
				case PdfSubpath.Move move:
					start = move.Location;
					current = move.Location;
					break;
				case PdfSubpath.Line line:
					output.Add(new PdfSegment { Start = map(line.From), End = map(line.To) });
					current = line.To;
					start ??= line.From;
					break;
				case PdfSubpath.CubicBezierCurve cubic:
					output.Add(new PdfSegment
					{
						Start = map(cubic.StartPoint),
						End = map(cubic.EndPoint),
						IsBezier = true,
						Control1 = map(cubic.FirstControlPoint),
						Control2 = map(cubic.SecondControlPoint)
					});
					current = cubic.EndPoint;
					start ??= cubic.StartPoint;
					break;
				case PdfSubpath.QuadraticBezierCurve quad:
				{
					// Cuadrática → cúbica equivalente.
					PdfPoint p0 = quad.StartPoint, q = quad.ControlPoint, p3 = quad.EndPoint;
					var c1 = new PdfPoint(p0.X + 2.0 / 3.0 * (q.X - p0.X), p0.Y + 2.0 / 3.0 * (q.Y - p0.Y));
					var c2 = new PdfPoint(p3.X + 2.0 / 3.0 * (q.X - p3.X), p3.Y + 2.0 / 3.0 * (q.Y - p3.Y));
					output.Add(new PdfSegment { Start = map(p0), End = map(p3), IsBezier = true, Control1 = map(c1), Control2 = map(c2) });
					current = p3;
					start ??= p0;
					break;
				}
				case PdfSubpath.Close:
					if (start.HasValue && current.HasValue && (start.Value.X != current.Value.X || start.Value.Y != current.Value.Y))
					{
						output.Add(new PdfSegment { Start = map(current.Value), End = map(start.Value) });
					}

					current = start;
					break;
			}
		}
	}

	private static List<Pt> Polygonize(PdfSubpath subpath, Func<PdfPoint, Pt> map)
	{
		var points = new List<Pt>();
		foreach (PdfSubpath.IPathCommand command in subpath.Commands)
		{
			switch (command)
			{
				case PdfSubpath.Move move:
					if (points.Count == 0)
					{
						points.Add(map(move.Location));
					}

					break;
				case PdfSubpath.Line line:
					if (points.Count == 0)
					{
						points.Add(map(line.From));
					}

					points.Add(map(line.To));
					break;
				case PdfSubpath.BezierCurve curve:
					foreach (PdfSubpath.Line piece in curve.ToLines(BezierSteps))
					{
						if (points.Count == 0)
						{
							points.Add(map(piece.From));
						}

						points.Add(map(piece.To));
					}

					break;
			}
		}

		return points;
	}

	private static double Area(List<List<Pt>> loops)
	{
		double total = 0.0;
		foreach (List<Pt> loop in loops)
		{
			double a = 0.0;
			for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
			{
				a += (loop[j].X + loop[i].X) * (loop[j].Y - loop[i].Y);
			}

			total += Math.Abs(a / 2.0);
		}

		return total;
	}

	private static (byte, byte, byte) ToRgb(IColor color)
	{
		try
		{
			if (color == null)
			{
				return (0, 0, 0);
			}

			(double r, double g, double b) = color.ToRGBValues();
			return (ToByte(r), ToByte(g), ToByte(b));
		}
		catch (Exception)
		{
			return (0, 0, 0);
		}
	}

	private static byte ToByte(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255.0)));

	// ------------------------------------------------------------------ Textos

	/// <summary>
	/// Agrupa las letras en líneas de texto: letras seguidas con el mismo tamaño y dirección, y separadas
	/// menos que un par de caracteres. Se añaden espacios donde el hueco entre letras lo indica.
	/// </summary>
	private static void ReadTexts(Page page, PdfDrawing drawing, Func<PdfPoint, Pt> map)
	{
		IReadOnlyList<Letter> letters;
		try
		{
			letters = page.Letters;
		}
		catch (Exception)
		{
			return;
		}

		PdfTextItem current = null;
		Pt lastEnd = default;
		double lastAngle = 0.0;
		var builder = new System.Text.StringBuilder();

		void Flush()
		{
			if (current != null)
			{
				current.Text = builder.ToString().Trim();
				if (current.Text.Length > 0)
				{
					drawing.Texts.Add(current);
				}
			}

			current = null;
			builder.Clear();
		}

		foreach (Letter letter in letters.OrderBy(l => l.TextSequence))
		{
			// Texto invisible (p.ej. capa de OCR de un PDF escaneado) no se dibuja.
			if (letter.RenderingMode == TextRenderingMode.Neither || string.IsNullOrEmpty(letter.Value))
			{
				continue;
			}

			Pt start = map(letter.StartBaseLine);
			Pt end = map(letter.EndBaseLine);
			double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
			double size = letter.PointSize > 0 ? letter.PointSize : letter.FontSize;
			if (size <= 0.0)
			{
				continue;
			}

			bool continues = false;
			if (current != null && Math.Abs(size - current.SizePt) <= current.SizePt * 0.1 && AngleDiff(angle, lastAngle) < 0.05)
			{
				// Distancia a lo largo de la línea y separación perpendicular respecto al final de la letra anterior.
				double dx = start.X - lastEnd.X, dy = start.Y - lastEnd.Y;
				double along = dx * Math.Cos(lastAngle) + dy * Math.Sin(lastAngle);
				double across = -dx * Math.Sin(lastAngle) + dy * Math.Cos(lastAngle);
				if (Math.Abs(across) < size * 0.3 && along > -size * 0.5 && along < size * 1.5)
				{
					continues = true;
					if (along > size * 0.2 && !letter.Value.StartsWith(" ", StringComparison.Ordinal) && builder.Length > 0 && builder[builder.Length - 1] != ' ')
					{
						builder.Append(' ');
					}
				}
			}

			if (!continues)
			{
				Flush();
				if (letter.Value.Trim().Length == 0)
				{
					continue;
				}

				string font = letter.FontName ?? string.Empty;
				current = new PdfTextItem
				{
					Origin = start,
					SizePt = size,
					Rotation = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) > 1E-06 ? angle : 0.0,
					FontName = NormalizeFont(font),
					Bold = font.IndexOf("bold", StringComparison.OrdinalIgnoreCase) >= 0 || font.IndexOf("black", StringComparison.OrdinalIgnoreCase) >= 0
				};
			}

			builder.Append(letter.Value);
			lastEnd = end;
			lastAngle = angle;
		}

		Flush();
	}

	private static double AngleDiff(double a, double b)
	{
		double d = Math.Abs(a - b) % (2.0 * Math.PI);
		return d > Math.PI ? 2.0 * Math.PI - d : d;
	}

	/// <summary>"ABCDEF+Arial-BoldMT" → "Arial"; fuentes desconocidas → null (se usa Arial).</summary>
	private static string NormalizeFont(string pdfFont)
	{
		if (string.IsNullOrEmpty(pdfFont))
		{
			return null;
		}

		string name = pdfFont.Contains("+") ? pdfFont.Substring(pdfFont.IndexOf('+') + 1) : pdfFont;
		string lower = name.ToLowerInvariant();
		if (lower.Contains("arialnarrow") || lower.Contains("arial narrow"))
		{
			return "Arial Narrow";
		}

		if (lower.Contains("arial") || lower.Contains("helvetica"))
		{
			return "Arial";
		}

		if (lower.Contains("times"))
		{
			return "Times New Roman";
		}

		if (lower.Contains("calibri"))
		{
			return "Calibri";
		}

		if (lower.Contains("courier"))
		{
			return "Courier New";
		}

		if (lower.Contains("isocp"))
		{
			return "ISOCPEUR";
		}

		if (lower.Contains("verdana"))
		{
			return "Verdana";
		}

		if (lower.Contains("tahoma"))
		{
			return "Tahoma";
		}

		if (lower.Contains("segoe"))
		{
			return "Segoe UI";
		}

		return null;
	}
}
