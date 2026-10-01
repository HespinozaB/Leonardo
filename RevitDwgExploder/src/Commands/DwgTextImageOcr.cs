using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitDwgExploder.Raster;
using Tesseract;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Último recurso: exporta una imagen del área del CAD y reconoce el texto por OCR (Tesseract).
/// </summary>
internal static class DwgTextImageOcr
{
	private const int ExportPixelSize = 3000;

	private const float MinConfidencePercent = 55f;

	private static readonly Regex HasAlphanumeric = new Regex("[A-Za-z0-9]", RegexOptions.Compiled);

	private static readonly object EngineLock = new object();

	/// <summary>Motores ya creados por idioma ("eng", "spa+eng"); null = no se pudo crear.</summary>
	private static readonly Dictionary<string, TesseractEngine> Engines = new Dictionary<string, TesseractEngine>();

	private static string DataPath => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "tessdata");

	/// <summary>Español + inglés si está el modelo de español (acentos, Ñ); si no, inglés.</summary>
	private static string TextLanguage => File.Exists(Path.Combine(DataPath, "spa.traineddata")) ? "spa+eng" : "eng";

	public static List<DwgTextImporter.DwgTextEntry> Recognize(Document doc, View view, ImportInstance importInstance)
	{
		var result = new List<DwgTextImporter.DwgTextEntry>();
		BoundingBoxXYZ bbox = importInstance.get_BoundingBox(view);
		if (bbox == null)
		{
			return result;
		}

		TesseractEngine engine = GetEngine();
		if (engine == null)
		{
			return result;
		}

		BoundingBoxXYZ originalCrop;
		try
		{
			originalCrop = view.CropBox;
		}
		catch (Exception)
		{
			return result;
		}

		if (originalCrop == null)
		{
			return result;
		}

		// La caja de recorte vive en coordenadas de la vista: pasamos las esquinas del CAD a ese sistema
		// para que funcione también en vistas giradas o con origen desplazado.
		Transform cropTransform = originalCrop.Transform ?? Transform.Identity;
		Transform toCrop = cropTransform.Inverse;
		double zMid = (bbox.Min.Z + bbox.Max.Z) / 2.0;
		var corners = new[]
		{
			toCrop.OfPoint(new XYZ(bbox.Min.X, bbox.Min.Y, zMid)),
			toCrop.OfPoint(new XYZ(bbox.Max.X, bbox.Min.Y, zMid)),
			toCrop.OfPoint(new XYZ(bbox.Min.X, bbox.Max.Y, zMid)),
			toCrop.OfPoint(new XYZ(bbox.Max.X, bbox.Max.Y, zMid))
		};
		double minX = corners.Min(c => c.X);
		double maxX = corners.Max(c => c.X);
		double minY = corners.Min(c => c.Y);
		double maxY = corners.Max(c => c.Y);
		double width = maxX - minX;
		double height = maxY - minY;
		if (width < 1E-06 || height < 1E-06)
		{
			return result;
		}

		minX -= width * 0.03;
		maxX += width * 0.03;
		minY -= height * 0.03;
		maxY += height * 0.03;
		width = maxX - minX;
		height = maxY - minY;

		string tempDir = Path.Combine(Path.GetTempPath(), "RevitDwgExploder_" + Guid.NewGuid().ToString("N"));
		const string baseName = "ocr";
		try
		{
			Directory.CreateDirectory(tempDir);
			string imagePath;

			// Todo el cambio de recorte ocurre dentro de un TransactionGroup que se deshace al final:
			// la vista queda intacta y no se ensucia el historial de deshacer.
			using (var group = new TransactionGroup(doc, "RevitDwgExploder: OCR"))
			{
				group.Start();
				try
				{
					using (var tx = new Transaction(doc, "RevitDwgExploder: ajustar recorte temporal"))
					{
						tx.Start();
						view.CropBoxActive = true;
						view.CropBoxVisible = false;
						view.CropBox = new BoundingBoxXYZ
						{
							Transform = cropTransform,
							Min = new XYZ(minX, minY, originalCrop.Min.Z),
							Max = new XYZ(maxX, maxY, originalCrop.Max.Z)
						};
						tx.Commit();
					}

					var options = new ImageExportOptions
					{
						FilePath = Path.Combine(tempDir, baseName),
						ZoomType = ZoomFitType.FitToPage,
						PixelSize = ExportPixelSize,
						ImageResolution = ImageResolution.DPI_300,
						FitDirection = FitDirectionType.Horizontal,
						ExportRange = ExportRange.SetOfViews,
						HLRandWFViewsFileType = ImageFileType.PNG
					};
					options.SetViewsAndSheets(new List<ElementId> { view.Id });
					doc.ExportImage(options);
				}
				finally
				{
					group.RollBack();
				}
			}

			imagePath = Directory.GetFiles(tempDir, baseName + "*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			if (imagePath == null)
			{
				return result;
			}

			double textRotation = Math.Atan2(cropTransform.BasisX.Y, cropTransform.BasisX.X);
			lock (EngineLock)
			{
				using Pix image = Pix.LoadFromFile(imagePath);
				if (image.Width <= 0 || image.Height <= 0)
				{
					return result;
				}

				double feetPerPixel = width / image.Width;
				double feetPerPixelY = height / image.Height;
				if (Math.Abs(feetPerPixelY - feetPerPixel) / feetPerPixel < 0.05)
				{
					feetPerPixel = (feetPerPixel + feetPerPixelY) / 2.0;
				}

				using Page page = engine.Process(image, PageSegMode.SparseText);
				using ResultIterator iterator = page.GetIterator();
				iterator.Begin();
				do
				{
					if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect bounds))
					{
						continue;
					}

					float confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
					if (confidence < MinConfidencePercent)
					{
						continue;
					}

					string text = CadTextCollector.CleanText(iterator.GetText(PageIteratorLevel.TextLine));
					if (text.Length == 0 || !HasAlphanumeric.IsMatch(text))
					{
						continue;
					}

					double x = minX + bounds.X1 * feetPerPixel;
					double yTop = maxY - bounds.Y1 * feetPerPixel;
					double yBottom = maxY - bounds.Y2 * feetPerPixel;
					double textHeight = Math.Abs(yTop - yBottom);
					if (textHeight < 0.0001)
					{
						continue;
					}

					XYZ world = cropTransform.OfPoint(new XYZ(x, Math.Min(yTop, yBottom), 0.0));
					result.Add(new DwgTextImporter.DwgTextEntry
					{
						Text = text,
						Position = new XYZ(world.X, world.Y, zMid),
						HeightFeet = textHeight,
						RotationRadians = textRotation,
						AnchorH = TextAnchorH.Left,
						AnchorV = TextAnchorV.Bottom
					});
				}
				while (iterator.Next(PageIteratorLevel.TextLine));
			}
		}
		catch (Exception)
		{
		}
		finally
		{
			try
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, recursive: true);
				}
			}
			catch (Exception)
			{
			}
		}

		return result;
	}

	/// <summary>
	/// Lee las líneas de texto de una imagen (ampliada <paramref name="scale"/> veces respecto a la original): las
	/// coordenadas se devuelven en píxeles de la imagen original. Vacío si el OCR no está disponible.
	/// </summary>
	public static List<RasterTextLine> RecognizeLines(string imagePath, int scale, string language = null)
	{
		var words = new List<RasterText.OcrWord>();
		TesseractEngine engine = GetEngine(language ?? TextLanguage);
		if (engine == null || !File.Exists(imagePath))
		{
			return new List<RasterTextLine>();
		}

		try
		{
			lock (EngineLock)
			{
				using Pix image = Pix.LoadFromFile(imagePath);
				using Page page = engine.Process(image, PageSegMode.SparseText);
				using ResultIterator iterator = page.GetIterator();
				iterator.Begin();
				int lineId = 0;
				double baseline = 0;
				do
				{
					if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine))
					{
						lineId++;
						baseline = double.NaN;
						if (iterator.TryGetBaseline(PageIteratorLevel.TextLine, out Rect line))
						{
							baseline = (line.Y1 + line.Y2) / 2.0;
						}
					}

					if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out Rect bounds))
					{
						continue;
					}

					string text = CadTextCollector.CleanText(iterator.GetText(PageIteratorLevel.Word));
					if (text.Length == 0)
					{
						continue;
					}

					double wordBaseline = double.IsNaN(baseline) ? bounds.Y2 : baseline;
					words.Add(new RasterText.OcrWord(
						text,
						bounds.X1 / scale,
						bounds.Y1 / scale,
						(bounds.X2 + scale - 1) / scale,
						(bounds.Y2 + scale - 1) / scale,
						iterator.GetConfidence(PageIteratorLevel.Word),
						lineId,
						wordBaseline / scale));
				}
				while (iterator.Next(PageIteratorLevel.Word));
			}
		}
		catch (Exception)
		{
		}

		return RasterText.GroupWords(words);
	}

	/// <summary>
	/// Lee cada zona (en píxeles de la imagen original) como una sola línea de texto. Devuelve solo las zonas con
	/// lectura; la caja es la de la zona.
	/// </summary>
	public static List<RasterTextLine> RecognizeRegions(string imagePath, int scale, IList<PixelRect> regions)
	{
		var result = new List<RasterTextLine>();
		if (regions.Count == 0 || !File.Exists(imagePath))
		{
			return result;
		}

		// Cada zona se lee con inglés y con español + inglés: en etiquetas cortas uno u otro acierta ("TIPO" en inglés,
		// "LÁMINAS" en español). Se queda la lectura creíble de mayor confianza.
		var engines = new[] { GetEngine("eng"), TextLanguage == "eng" ? null : GetEngine(TextLanguage) }.Where(e => e != null).ToList();
		if (engines.Count == 0)
		{
			return result;
		}

		try
		{
			lock (EngineLock)
			{
				using Pix image = Pix.LoadFromFile(imagePath);
				foreach (PixelRect r in regions)
				{
					const int margin = 3;
					int x1 = Math.Max(0, (r.X1 - margin) * scale), y1 = Math.Max(0, (r.Y1 - margin) * scale);
					int x2 = Math.Min(image.Width - 1, (r.X2 + margin) * scale), y2 = Math.Min(image.Height - 1, (r.Y2 + margin) * scale);
					if (x2 <= x1 || y2 <= y1)
					{
						continue;
					}

					string bestText = null;
					float bestConfidence = -1f;
					bool bestPlausible = false;
					foreach (TesseractEngine engine in engines)
					{
						using Page page = engine.Process(image, Rect.FromCoords(x1, y1, x2, y2), PageSegMode.SingleLine);
						string text = CadTextCollector.CleanText(page.GetText() ?? string.Empty);
						if (text.Length == 0)
						{
							continue;
						}

						float confidence = page.GetMeanConfidence() * 100f;
						bool plausible = RasterText.IsPlausible(text, confidence);
						if ((plausible && !bestPlausible) || (plausible == bestPlausible && confidence > bestConfidence))
						{
							bestText = text;
							bestConfidence = confidence;
							bestPlausible = plausible;
						}
					}

					if (bestText == null)
					{
						continue;
					}

					result.Add(new RasterTextLine
					{
						Text = bestText,
						Confidence = bestConfidence,
						X1 = r.X1,
						Y1 = r.Y1,
						X2 = r.X2 + 1,
						Y2 = r.Y2 + 1,
						BaselineY = r.Y2 + 1
					});
				}
			}
		}
		catch (Exception)
		{
		}

		return result;
	}

	private static TesseractEngine GetEngine(string language = "eng")
	{
		lock (EngineLock)
		{
			if (Engines.TryGetValue(language, out TesseractEngine existing))
			{
				return existing;
			}

			TesseractEngine engine = null;
			try
			{
				if (Directory.Exists(DataPath))
				{
					engine = new TesseractEngine(DataPath, language, EngineMode.Default);
				}
			}
			catch (Exception)
			{
				engine = null;
			}

			Engines[language] = engine;
			return engine;
		}
	}

	public static void DisposeEngine()
	{
		lock (EngineLock)
		{
			foreach (TesseractEngine engine in Engines.Values)
			{
				engine?.Dispose();
			}

			Engines.Clear();
		}
	}
}
