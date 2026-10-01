using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
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

	private static TesseractEngine _engine;

	private static bool _engineFailed;

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

	/// <summary>Línea de texto reconocida en una imagen (píxeles, origen arriba a la izquierda).</summary>
	internal readonly struct OcrLine
	{
		public readonly string Text;
		public readonly int X1;
		public readonly int Y1;
		public readonly int X2;
		public readonly int Y2;

		public OcrLine(string text, int x1, int y1, int x2, int y2)
		{
			Text = text;
			X1 = x1;
			Y1 = y1;
			X2 = x2;
			Y2 = y2;
		}
	}

	/// <summary>Reconoce las líneas de texto de un archivo de imagen (vacío si el OCR no está disponible).</summary>
	public static List<OcrLine> RecognizeFile(string imagePath)
	{
		var result = new List<OcrLine>();
		TesseractEngine engine = GetEngine();
		if (engine == null || !File.Exists(imagePath))
		{
			return result;
		}

		try
		{
			lock (EngineLock)
			{
				using Pix image = Pix.LoadFromFile(imagePath);
				using Page page = engine.Process(image, PageSegMode.SparseText);
				using ResultIterator iterator = page.GetIterator();
				iterator.Begin();
				do
				{
					if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect bounds)
						|| iterator.GetConfidence(PageIteratorLevel.TextLine) < MinConfidencePercent)
					{
						continue;
					}

					string text = CadTextCollector.CleanText(iterator.GetText(PageIteratorLevel.TextLine));
					if (text.Length == 0 || !HasAlphanumeric.IsMatch(text))
					{
						continue;
					}

					result.Add(new OcrLine(text, bounds.X1, bounds.Y1, bounds.X2, bounds.Y2));
				}
				while (iterator.Next(PageIteratorLevel.TextLine));
			}
		}
		catch (Exception)
		{
		}

		return result;
	}

	private static TesseractEngine GetEngine()
	{
		lock (EngineLock)
		{
			if (_engine != null || _engineFailed)
			{
				return _engine;
			}

			try
			{
				string dataPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "tessdata");
				if (!Directory.Exists(dataPath))
				{
					_engineFailed = true;
					return null;
				}

				_engine = new TesseractEngine(dataPath, "eng", EngineMode.Default);
				return _engine;
			}
			catch (Exception)
			{
				_engineFailed = true;
				return null;
			}
		}
	}

	public static void DisposeEngine()
	{
		lock (EngineLock)
		{
			_engine?.Dispose();
			_engine = null;
		}
	}
}
