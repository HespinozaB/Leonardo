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

	/// <summary>Grosores de pluma estándar (mm de papel) a los que se ajustan los trazos para no crear un estilo por píxel.</summary>
	private static readonly double[] StandardWidthsMm = { 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 };

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

		// Se trabaja a un tamaño acotado; el píxel crece en la misma proporción.
		double factor = Math.Min(1.0, (double)MaxSide / Math.Max(bitmap.Width, bitmap.Height));
		int width = Math.Max(1, (int)Math.Round(bitmap.Width * factor));
		int height = Math.Max(1, (int)Math.Round(bitmap.Height * factor));
		double feetPerPx = feetPerPixel * bitmap.Width / width;

		uint[] argb;
		List<DwgTextImageOcr.OcrLine> texts;
		string tempFile = Path.Combine(Path.GetTempPath(), "EMASY_img_" + Guid.NewGuid().ToString("N") + ".png");
		try
		{
			using (Bitmap work = Normalize(bitmap, width, height))
			{
				argb = Pixels(work);
				work.Save(tempFile, ImageFormat.Png);
			}

			texts = DwgTextImageOcr.RecognizeFile(tempFile)
				.Where(t => t.Y2 - t.Y1 >= 6 && t.Y2 - t.Y1 <= height * 0.08)
				.ToList();
		}
		finally
		{
			try
			{
				File.Delete(tempFile);
			}
			catch (Exception)
			{
			}
		}

		// Los píxeles de los textos reconocidos no se vectorizan (serían cientos de trazos cortos).
		IEnumerable<PixelRect> ignore = texts.Select(t => new PixelRect(t.X1 - 2, t.Y1 - 2, t.X2 + 2, t.Y2 + 2));
		RasterDrawing raster = RasterVectorizer.Vectorize(width, height, argb, ignore);
		PdfDrawing drawing = ToDrawing(raster, texts, feetPerPx, Math.Max(1, view.Scale));
		return PdfExploder.Explode(doc, view, drawing, bottomLeft, feetPerPx, shortCurveTolerance, stats, StylePrefix);
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
	/// izquierda). Los grosores se expresan en puntos de papel según la escala de la vista, igual que en un PDF.
	/// </summary>
	private static PdfDrawing ToDrawing(RasterDrawing raster, List<DwgTextImageOcr.OcrLine> texts, double feetPerPx, int viewScale)
	{
		double h = raster.Height;
		var drawing = new PdfDrawing { Width = raster.Width, Height = raster.Height };

		foreach (RasterFill fill in raster.Fills)
		{
			if (fill.Outline.Count < 3)
			{
				continue;
			}

			var shape = new PdfFillShape { R = fill.R, G = fill.G, B = fill.B };
			shape.Loops.Add(fill.Outline.Select(p => new Pt(p.X, h - p.Y)).ToList());
			drawing.Fills.Add(shape);
		}

		var strokes = new Dictionary<PdfStrokeStyle, PdfStroke>();
		foreach (RasterLine line in raster.Lines)
		{
			(byte r, byte g, byte b) = QuantizeColor(line.R, line.G, line.B);
			double paperMm = Math.Max(1.0, line.Thickness) * feetPerPx * 304.8 / viewScale;
			double widthMm = StandardWidthsMm.OrderBy(w => Math.Abs(w - paperMm)).First();
			var style = new PdfStrokeStyle(r, g, b, widthMm * 72.0 / 25.4, string.Empty);
			if (!strokes.TryGetValue(style, out PdfStroke stroke))
			{
				stroke = new PdfStroke { Style = style };
				strokes[style] = stroke;
				drawing.Strokes.Add(stroke);
			}

			stroke.Segments.Add(new PdfSegment { Start = new Pt(line.X1, h - line.Y1), End = new Pt(line.X2, h - line.Y2) });
		}

		foreach (DwgTextImageOcr.OcrLine text in texts)
		{
			// La caja del OCR va de lo alto de las mayúsculas a lo bajo de los descendentes: ~0.75 es la altura de mayúsculas.
			double boxHeight = text.Y2 - text.Y1;
			drawing.Texts.Add(new PdfTextItem
			{
				Text = text.Text,
				Origin = new Pt(text.X1, h - text.Y2 + boxHeight * 0.12),
				SizePt = boxHeight * 0.75 / 0.72,
				Rotation = 0.0,
				FontName = "Arial",
				Bold = false
			});
		}

		return drawing;
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
