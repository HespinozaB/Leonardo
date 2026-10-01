using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Commands;
using RevitDwgExploder.Pdf;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Raster;

/// <summary>
/// Explota imágenes (PNG, JPG, BMP, TIF…) en elementos nativos: vectoriza la imagen (rellenos de color y trazos),
/// reconoce los textos por OCR y lo crea todo con el mismo motor que los PDF (Filled Regions, Detail Lines con
/// Line Styles "IMG …" y TextNotes).
/// </summary>
internal static class RasterExploder
{
	public const string StylePrefix = "IMG";

	public const string FileFilter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif";

	/// <summary>Lado máximo (píxeles) con el que se vectoriza: imágenes más grandes se reducen (tiempo y memoria).</summary>
	private const int MaxSide = 3200;

	/// <summary>Imágenes con el lado mayor hasta este tamaño se amplían 2× para vectorizarlas (baja resolución).</summary>
	private const int EnhanceBelow = 1600;

	/// <summary>Grosores de pluma (mm de papel) de los trazos, del más fino al más grueso.</summary>
	private static readonly double[] StandardWidthsMm = { 0.13, 0.18, 0.25, 0.35 };

	/// <summary>Imagen de Revit que no es un PDF (los PDF tienen su propio comando).</summary>
	public static bool IsRaster(ImageType type) => type != null && !PdfExploder.IsPdf(type);

	/// <summary>Bitmap de un tipo de imagen: el que guarda Revit o, si no se puede, el archivo.</summary>
	public static Bitmap LoadBitmap(ImageType type, string path)
	{
		try
		{
			Bitmap image = type?.GetImage();
			if (image != null && image.Width > 0 && image.Height > 0)
			{
				return image;
			}
		}
		catch (Exception)
		{
		}

		return !string.IsNullOrEmpty(path) && File.Exists(path) ? LoadFile(path) : null;
	}

	/// <summary>Carga un archivo de imagen sin dejarlo bloqueado.</summary>
	public static Bitmap LoadFile(string path)
	{
		using var stream = new MemoryStream(File.ReadAllBytes(path));
		using var image = Image.FromStream(stream);
		return new Bitmap(image);
	}

	/// <summary>Resolución (ppp) guardada en el archivo, o 96 si no la tiene o es absurda.</summary>
	public static int DpiOf(Bitmap bitmap)
	{
		float dpi = bitmap?.HorizontalResolution ?? 96f;
		return dpi >= 50f && dpi <= 2400f ? (int)Math.Round(dpi) : 96;
	}

	/// <summary>
	/// Explota <paramref name="bitmap"/> en <paramref name="view"/>: <paramref name="bottomLeft"/> es el punto del modelo
	/// de la esquina inferior izquierda de la imagen y <paramref name="feetPerPixel"/> el tamaño de un píxel.
	/// </summary>
	public static bool Explode(Document doc, View view, Bitmap bitmap, XYZ bottomLeft, double feetPerPixel, double shortCurveTolerance, PdfExplodeStats stats)
	{
		if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || feetPerPixel <= 0.0)
		{
			return false;
		}

		// Calidad de imagen: una imagen de baja resolución se amplía 2× (bicúbico) antes de vectorizar; las líneas
		// finas y los rellenos estrechos salen continuos y rectos. Las grandes se reducen hasta un tamaño acotado.
		int longest = Math.Max(bitmap.Width, bitmap.Height);
		double enhancement = longest <= EnhanceBelow ? 2.0 : 1.0;
		double factor = Math.Min(enhancement, (double)MaxSide / longest);
		int width = Math.Max(1, (int)Math.Round(bitmap.Width * factor));
		int height = Math.Max(1, (int)Math.Round(bitmap.Height * factor));
		double feetPerPx = feetPerPixel * bitmap.Width / width;

		uint[] argb;
		using (Bitmap work = Normalize(bitmap, width, height))
		{
			argb = Pixels(work);
		}

		// Lado mínimo de un relleno en píxeles: Revit no admite tramos más cortos que su tolerancia.
		double minSegment = shortCurveTolerance * 1.6 / feetPerPx;

		// 1ª pasada: líneas y rellenos, para preparar las imágenes del OCR.
		double pixelScale = Math.Max(1.0, factor);
		RasterDrawing firstPass = RasterVectorizer.Vectorize(width, height, argb, null, minSegment: minSegment, pixelScale: pixelScale);
		List<RasterTextLine> texts = ReadTexts(width, height, argb, firstPass, pixelScale);

		// 2ª pasada: sin los píxeles de los textos reconocidos (serían cientos de trazos cortos).
		IEnumerable<PixelRect> ignore = texts.Select(RasterText.EraseArea);
		RasterDrawing raster = RasterVectorizer.Vectorize(width, height, argb, ignore, minSegment: minSegment, pixelScale: pixelScale);
		PdfDrawing drawing = ToDrawing(raster, texts);
		return PdfExploder.Explode(doc, view, drawing, bottomLeft, feetPerPx, shortCurveTolerance, stats, StylePrefix);
	}

	/// <summary>
	/// OCR en tres lecturas: la imagen tal cual, la imagen "limpia" (sin rellenos, líneas largas ni grises claros) y,
	/// una a una, las zonas con forma de texto que no se leyeron. Se queda la lectura más segura de cada zona.
	/// </summary>
	private static List<RasterTextLine> ReadTexts(int width, int height, uint[] argb, RasterDrawing firstPass, double pixelScale)
	{
		// El OCR lee a ~2× de la imagen original: si ya se amplió para vectorizar, no se vuelve a ampliar.
		int scale = pixelScale >= 2.0 ? 1 : RasterText.OcrScale(width, height);
		string dir = Path.Combine(Path.GetTempPath(), "EMASY_img_" + Guid.NewGuid().ToString("N"));
		var candidates = new List<RasterTextLine>();
		try
		{
			Directory.CreateDirectory(dir);
			string plainPath = Path.Combine(dir, "plain.pgm");
			string cleanPath = Path.Combine(dir, "clean.pgm");
			byte[] plain = RasterText.OcrGray(width, height, argb, firstPass, clean: false);
			byte[] clean = RasterText.OcrGray(width, height, argb, firstPass, clean: true);
			RasterText.WritePgm(plainPath, RasterText.Upscale(plain, width, height, scale, out int pw, out int ph), pw, ph);
			RasterText.WritePgm(cleanPath, RasterText.Upscale(clean, width, height, scale, out int cw, out int ch), cw, ch);

			// 1) Lectura de página de las dos imágenes con inglés y con español + inglés (tildes, Ñ): sirve sobre todo
			//    para encontrar dónde hay texto.
			foreach (string path in new[] { plainPath, cleanPath })
			{
				candidates.AddRange(DwgTextImageOcr.RecognizeLines(path, scale, "eng"));
				candidates.AddRange(DwgTextImageOcr.RecognizeLines(path, scale));
			}

			RasterText.PlanReading(candidates, height, out List<RasterTextLine> lines, out List<PixelRect> reread, out List<RasterTextLine> fallback);

			// 2) Las zonas dudosas y 3) las zonas con forma de texto que nadie leyó se leen una a una, en las dos
			//    imágenes y con los dos idiomas; gana la lectura mejor puntuada.
			var found = new List<RasterTextLine>(lines.Concat(fallback));
			foreach (string path in new[] { plainPath, cleanPath })
			{
				found.AddRange(Accepted(DwgTextImageOcr.RecognizeRegions(path, scale, reread), height));
			}

			found = RasterText.Merge(found);
			List<PixelRect> regions = RasterText.FindTextCandidates(clean, width, height, found);
			foreach (string path in new[] { plainPath, cleanPath })
			{
				found.AddRange(Accepted(DwgTextImageOcr.RecognizeRegions(path, scale, regions), height));
			}

			lines = RasterText.Merge(found);
			foreach (RasterTextLine line in lines)
			{
				line.Text = TextCorrector.Correct(line.Text, line.Confidence);
			}

			RasterText.FitTexts(lines, plain, width, height);
			return lines;
		}
		catch (Exception)
		{
			return new List<RasterTextLine>();
		}
		finally
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (Exception)
			{
			}
		}
	}

	/// <summary>Lecturas creíbles y de tamaño de texto, con los códigos de plano corregidos (co2 → C02).</summary>
	private static IEnumerable<RasterTextLine> Accepted(IEnumerable<RasterTextLine> lines, int height)
	{
		foreach (RasterTextLine line in lines)
		{
			int boxHeight = line.Y2 - line.Y1;
			if (boxHeight < 5 || boxHeight > height * 0.08 || !RasterText.IsPlausible(line.Text, line.Confidence)
				|| !RasterText.FitsBox(line))
			{
				continue;
			}

			line.Text = RasterText.FixCodes(line.Text).Trim();
			yield return line;
		}
	}

	/// <summary>Explota una imagen insertada en Revit en su lugar (misma posición y tamaño).</summary>
	public static bool ExplodeImage(Document doc, ImageInstance instance, string path, double shortCurveTolerance, PdfExplodeStats stats)
	{
		var type = doc.GetElement(instance.GetTypeId()) as ImageType;
		var view = doc.GetElement(instance.OwnerViewId) as View;
		if (type == null || view == null || instance.Width <= 0.0)
		{
			return false;
		}

		using Bitmap bitmap = LoadBitmap(type, path);
		if (bitmap == null)
		{
			return false;
		}

		XYZ origin = instance.GetLocation(BoxPlacement.BottomLeft);
		return Explode(doc, view, bitmap, origin, instance.Width / bitmap.Width, shortCurveTolerance, stats);
	}

	/// <summary>Pies de modelo por píxel para una imagen de <paramref name="dpi"/> ppp dibujada a escala 1:<paramref name="scale"/>.</summary>
	public static double FeetPerPixel(int dpi, int scale) => scale / (Math.Max(1, dpi) * 12.0);

	/// <summary>Copia a 32 bpp con fondo blanco (las transparencias pasan a ser fondo), reduciendo si hace falta.</summary>
	private static Bitmap Normalize(Bitmap source, int width, int height)
	{
		var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
		result.SetResolution(96f, 96f);
		using Graphics g = Graphics.FromImage(result);
		g.Clear(Color.White);
		g.InterpolationMode = width == source.Width && height == source.Height ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
		g.PixelOffsetMode = PixelOffsetMode.Half;
		g.DrawImage(source, new Rectangle(0, 0, width, height));
		return result;
	}

	private static uint[] Pixels(Bitmap bitmap)
	{
		var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
		BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			var raw = new int[bitmap.Width * bitmap.Height];
			if (data.Stride == bitmap.Width * 4)
			{
				Marshal.Copy(data.Scan0, raw, 0, raw.Length);
			}
			else
			{
				for (int y = 0; y < bitmap.Height; y++)
				{
					Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * bitmap.Width, bitmap.Width);
				}
			}

			var result = new uint[raw.Length];
			Buffer.BlockCopy(raw, 0, result, 0, raw.Length * 4);
			return result;
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
	}

	/// <summary>
	/// Pasa el resultado de la vectorización al modelo de dibujo de los PDF, en unidades de píxel (origen abajo a la
	/// izquierda). Los grosores son relativos al trazo fino típico de la imagen (plumas de 0.13 a 0.35 mm).
	/// </summary>
	private static PdfDrawing ToDrawing(RasterDrawing raster, List<RasterTextLine> texts)
	{
		double h = raster.Height;
		var drawing = new PdfDrawing { Width = raster.Width, Height = raster.Height };

		foreach (RasterFill fill in raster.Fills)
		{
			if (fill.Outline.Count < 3)
			{
				continue;
			}

			var shape = new PdfFillShape { R = fill.R, G = fill.G, B = fill.B, OuterFirst = true };
			shape.Loops.Add(fill.Outline.Select(p => new Pt(p.X, h - p.Y)).ToList());
			foreach (List<(double X, double Y)> hole in fill.Holes.Where(l => l.Count >= 3))
			{
				shape.Loops.Add(hole.Select(p => new Pt(p.X, h - p.Y)).ToList());
			}

			drawing.Fills.Add(shape);
		}

		// Grosores relativos: el trazo fino típico de la imagen es la pluma más fina y los demás se escalan respecto a
		// él (con tope), así un plano escaneado a baja resolución no sale con líneas gruesas.
		double baseThickness = TypicalThickness(raster.Lines);
		var strokes = new Dictionary<PdfStrokeStyle, PdfStroke>();
		foreach (RasterLine line in raster.Lines)
		{
			(byte r, byte g, byte b) = QuantizeColor(line.R, line.G, line.B);
			double ratio = line.Thickness / baseThickness;
			double widthMm = ratio < 1.6 ? StandardWidthsMm[0] : ratio < 2.6 ? StandardWidthsMm[1] : ratio < 4.0 ? StandardWidthsMm[2] : StandardWidthsMm[3];
			var style = new PdfStrokeStyle(r, g, b, widthMm * 72.0 / 25.4, string.Empty);
			if (!strokes.TryGetValue(style, out PdfStroke stroke))
			{
				stroke = new PdfStroke { Style = style };
				strokes[style] = stroke;
				drawing.Strokes.Add(stroke);
			}

			stroke.Segments.Add(new PdfSegment { Start = new Pt(line.X1, h - line.Y1), End = new Pt(line.X2, h - line.Y2) });
		}

		foreach (RasterTextLine text in texts)
		{
			drawing.Texts.Add(new PdfTextItem
			{
				Text = text.Text,
				Origin = new Pt(text.X1, h - text.BaselineY),
				SizePt = text.EmPx,
				Rotation = 0.0,
				FontName = "Arial",
				Bold = text.Bold,
				WidthFactor = text.WidthFactor
			});
		}

		return drawing;
	}

	/// <summary>Grosor del trazo fino típico: percentil 30 de los grosores, ponderado por la longitud.</summary>
	private static double TypicalThickness(List<RasterLine> lines)
	{
		var items = lines
			.Select(l => (l.Thickness, Length: Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1))))
			.OrderBy(t => t.Thickness)
			.ToList();
		double total = items.Sum(t => t.Length), accumulated = 0;
		foreach (var item in items)
		{
			accumulated += item.Length;
			if (accumulated >= total * 0.3)
			{
				return Math.Max(1.0, item.Thickness);
			}
		}

		return 1.0;
	}

	/// <summary>Agrupa colores parecidos (los promedios de píxeles varían un poco) para no crear un Line Style por tono.</summary>
	private static (byte, byte, byte) QuantizeColor(byte r, byte g, byte b)
	{
		int max = Math.Max(r, Math.Max(g, b));
		int min = Math.Min(r, Math.Min(g, b));
		if (max - min < 40)
		{
			// Grises: negro, gris oscuro, gris medio, gris claro.
			int gray = (r + g + b) / 3;
			byte level = gray < 70 ? (byte)0 : gray < 130 ? (byte)96 : gray < 185 ? (byte)160 : (byte)208;
			return (level, level, level);
		}

		static byte Step(byte v) => (byte)Math.Min(255, (int)Math.Round(v / 51.0) * 51);
		return (Step(r), Step(g), Step(b));
	}

	/// <summary>Resumen de una o varias explotaciones de imágenes.</summary>
	public static void ShowSummary(PdfExplodeStats s, string title)
	{
		string text =
			$"Imágenes procesadas: {s.Pages}\n" +
			(s.CreatedViews > 0 ? $"Vistas de dibujo creadas: {s.CreatedViews}\n" : string.Empty) +
			$"Líneas: {s.Lines}\n" +
			$"Rellenos (Filled Regions): {s.Fills}" + (s.FailedFills > 0 ? $"  ({s.FailedFills} no se pudieron crear)" : string.Empty) + "\n" +
			$"Textos reconocidos (OCR): {s.Texts}\n" +
			(s.Skipped > 0 ? $"Tramos omitidos (demasiado cortos o inválidos): {s.Skipped}\n" : string.Empty) +
			(s.CreatedStyles > 0 ? $"Line Styles creados (prefijo \"{StylePrefix}\"): {s.CreatedStyles}\n" : string.Empty) +
			(s.Failed.Count > 0 ? $"\nNo se pudieron procesar ({s.Failed.Count}): " + string.Join(", ", s.Failed.Take(8)) + (s.Failed.Count > 8 ? "…" : string.Empty) + "\n" : string.Empty) +
			(s.SuppressedDialogs > 0 ? $"{s.SuppressedDialogs} aviso(s) de Revit se cancelaron automáticamente.\n" : string.Empty) +
			(s.DeletedOriginals > 0 ? $"{s.DeletedOriginals} imagen(es) original(es) eliminada(s) después de explotarlas.\n" : string.Empty) +
			"\nLa imagen se vectoriza: funciona mejor con planos limpios (fondo claro, buena resolución). " +
			"Fotos, degradados y escaneos borrosos dan resultados aproximados.";
		TaskDialog.Show(title, text);
	}
}
