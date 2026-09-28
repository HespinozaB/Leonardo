using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Captura una imagen de la zona del CAD tal como Revit la muestra (antes de explotar) para leer el color
/// real de cada relleno. Es la fuente de color más fiable: no depende de cómo Revit agrupe los rellenos
/// solapados, ni de materiales, capas o colores "por bloque".
/// </summary>
internal sealed class ViewColorSampler : IDisposable
{
	private const int PixelSize = 3000;

	private readonly Bitmap _bitmap;

	private readonly Transform _toCrop;

	private readonly double _minX;

	private readonly double _maxY;

	private readonly double _feetPerPixel;

	private ViewColorSampler(Bitmap bitmap, Transform toCrop, double minX, double maxY, double feetPerPixel)
	{
		_bitmap = bitmap;
		_toCrop = toCrop;
		_minX = minX;
		_maxY = maxY;
		_feetPerPixel = feetPerPixel;
	}

	/// <summary>Exporta la zona del CAD a imagen. Devuelve null si no se pudo (la vista queda intacta).</summary>
	public static ViewColorSampler Capture(Document doc, View view, ImportInstance instance)
	{
		BoundingBoxXYZ bbox = instance.get_BoundingBox(view);
		BoundingBoxXYZ originalCrop;
		try
		{
			originalCrop = view.CropBox;
		}
		catch (Exception)
		{
			return null;
		}

		if (bbox == null || originalCrop == null)
		{
			return null;
		}

		Transform cropTransform = originalCrop.Transform ?? Transform.Identity;
		Transform toCrop = cropTransform.Inverse;
		double z = (bbox.Min.Z + bbox.Max.Z) / 2.0;
		XYZ[] corners =
		{
			toCrop.OfPoint(new XYZ(bbox.Min.X, bbox.Min.Y, z)),
			toCrop.OfPoint(new XYZ(bbox.Max.X, bbox.Min.Y, z)),
			toCrop.OfPoint(new XYZ(bbox.Min.X, bbox.Max.Y, z)),
			toCrop.OfPoint(new XYZ(bbox.Max.X, bbox.Max.Y, z))
		};
		double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X);
		double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
		double width = maxX - minX, height = maxY - minY;
		if (width < 1E-06 || height < 1E-06)
		{
			return null;
		}

		minX -= width * 0.02;
		maxX += width * 0.02;
		minY -= height * 0.02;
		maxY += height * 0.02;
		width = maxX - minX;
		height = maxY - minY;

		string tempDir = Path.Combine(Path.GetTempPath(), "RevitDwgExploder_" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(tempDir);
			using (var group = new TransactionGroup(doc, "EMASY: captura de colores"))
			{
				group.Start();
				try
				{
					using (var tx = new Transaction(doc, "EMASY: recorte temporal"))
					{
						tx.Start();

						// Solo el CAD en la imagen: se ocultan temporalmente los demás elementos de la vista
						// (cotas, textos, rejillas, muros…), que además agrandarían la imagen fuera del recorte.
						List<ElementId> others = new FilteredElementCollector(doc, view.Id)
							.WhereElementIsNotElementType()
							.Where(e => e.Id != instance.Id && e.CanBeHidden(view))
							.Select(e => e.Id)
							.ToList();
						if (others.Count > 0)
						{
							try
							{
								view.HideElements(others);
							}
							catch (Exception)
							{
							}
						}

						try
						{
							Parameter annotationCrop = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
							if (annotationCrop != null && !annotationCrop.IsReadOnly)
							{
								annotationCrop.Set(1);
							}
						}
						catch (Exception)
						{
						}

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

					var exportOptions = new ImageExportOptions
					{
						FilePath = Path.Combine(tempDir, "colors"),
						ZoomType = ZoomFitType.FitToPage,
						PixelSize = PixelSize,
						ImageResolution = ImageResolution.DPI_150,
						FitDirection = width >= height ? FitDirectionType.Horizontal : FitDirectionType.Vertical,
						ExportRange = ExportRange.SetOfViews,
						HLRandWFViewsFileType = ImageFileType.PNG,
						ShadowViewsFileType = ImageFileType.PNG
					};
					exportOptions.SetViewsAndSheets(new List<ElementId> { view.Id });
					doc.ExportImage(exportOptions);
				}
				finally
				{
					group.RollBack();
				}
			}

			string file = Directory.GetFiles(tempDir, "colors*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			if (file == null)
			{
				return null;
			}

			Bitmap bitmap;
			using (var loaded = new Bitmap(file))
			{
				bitmap = new Bitmap(loaded);
			}

			// La imagen debe tener las proporciones del área recortada; si no (Revit añadió márgenes u otro
			// contenido), la correspondencia punto → píxel no es fiable y no se usa.
			double fppX = width / bitmap.Width;
			double fppY = height / bitmap.Height;
			if (Math.Abs(fppX - fppY) > Math.Max(fppX, fppY) * 0.03)
			{
				bitmap.Dispose();
				return null;
			}

			return new ViewColorSampler(bitmap, toCrop, minX, maxY, (fppX + fppY) / 2.0);
		}
		catch (Exception)
		{
			return null;
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
	}

	/// <summary>
	/// Color que Revit muestra en ese punto (moda de un pequeño entorno, ignorando las líneas finas).
	/// Null si no hay un color claramente dominante.
	/// </summary>
	public Color Sample(XYZ world)
	{
		if (world == null)
		{
			return null;
		}

		XYZ p = _toCrop.OfPoint(world);
		int cx = (int)Math.Round((p.X - _minX) / _feetPerPixel);
		int cy = (int)Math.Round((_maxY - p.Y) / _feetPerPixel);
		var counts = new Dictionary<int, (int N, int R, int G, int B)>();
		int total = 0;
		for (int dx = -3; dx <= 3; dx++)
		{
			for (int dy = -3; dy <= 3; dy++)
			{
				int x = cx + dx, y = cy + dy;
				if (x < 0 || y < 0 || x >= _bitmap.Width || y >= _bitmap.Height)
				{
					continue;
				}

				DrawingColor c = _bitmap.GetPixel(x, y);
				total++;
				// El blanco también cuenta: un relleno blanco (máscara) es un color válido.
				if (c.A < 128)
				{
					continue;
				}

				// Se agrupan tonos casi iguales (antialiasing de la exportación).
				int key = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
				counts[key] = counts.TryGetValue(key, out var acc) ? (acc.N + 1, acc.R + c.R, acc.G + c.G, acc.B + c.B) : (1, c.R, c.G, c.B);
			}
		}

		if (counts.Count == 0 || total == 0)
		{
			return null;
		}

		var best = counts.Values.OrderByDescending(v => v.N).First();
		if (best.N < total / 3)
		{
			return null;
		}

		return new Color((byte)(best.R / best.N), (byte)(best.G / best.N), (byte)(best.B / best.N));
	}

	public void Dispose()
	{
		_bitmap?.Dispose();
	}
}
