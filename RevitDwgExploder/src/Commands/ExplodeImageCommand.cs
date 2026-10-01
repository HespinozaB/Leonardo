using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Pdf;
using RevitDwgExploder.Raster;
using RevitDwgExploder.UI;
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

/// <summary>
/// "Explotar Imagen Actual": explota las imágenes (PNG, JPG…) insertadas en la vista actual (o las seleccionadas) en
/// su lugar; si no hay ninguna, permite elegir un archivo de imagen e importarlo como elementos nativos.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ExplodeImageCommand : IExternalCommand
{
	private const string Title = "Explotar Imagen";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument uiDoc = commandData.Application.ActiveUIDocument;
		Document doc = uiDoc.Document;
		double shortCurve = commandData.Application.Application.ShortCurveTolerance;

		List<ImageInstance> targets = RasterImagesIn(uiDoc);
		var stats = new PdfExplodeStats();
		if (targets.Count > 0)
		{
			using (var group = new TransactionGroup(doc, "EMASY: explotar imagen"))
			{
				group.Start();
				foreach (ImageInstance instance in targets)
				{
					var type = doc.GetElement(instance.GetTypeId()) as ImageType;
					string name = type?.Name ?? instance.Id.ToString();
					try
					{
						if (!RasterExploder.ExplodeImage(doc, instance, PathOf(doc, type), shortCurve, stats))
						{
							stats.Failed.Add(name);
						}
					}
					catch (Exception ex)
					{
						stats.Failed.Add(name + " (" + ex.Message + ")");
					}
				}

				group.Assimilate();
			}

			RasterExploder.ShowSummary(stats, Title + " — resumen");
			return Result.Succeeded;
		}

		// No hay imágenes en la vista: importar un archivo.
		var ask = new TaskDialog(Title)
		{
			MainInstruction = "No hay imágenes insertadas en la vista actual",
			MainContent = "Puedes elegir una imagen (PNG, JPG, BMP, TIF…) de un plano y convertirla en líneas, rellenos y " +
				"textos nativos de Revit (en una vista de dibujo nueva o en la vista actual).",
			CommonButtons = TaskDialogCommonButtons.None
		};
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Elegir un archivo de imagen…");
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancelar");
		if (ask.Show() != TaskDialogResult.CommandLink1)
		{
			return Result.Cancelled;
		}

		string file;
		using (var dialog = new OpenFileDialog { Title = "Selecciona la imagen a importar", Filter = RasterExploder.FileFilter, CheckFileExists = true })
		{
			if (dialog.ShowDialog() != DialogResult.OK)
			{
				return Result.Cancelled;
			}

			file = dialog.FileName;
		}

		return ImportFile(uiDoc, file, shortCurve, stats) ? Result.Succeeded : Result.Cancelled;
	}

	/// <summary>Pide resolución/escala/destino e importa la imagen. Devuelve false si el usuario canceló.</summary>
	private static bool ImportFile(UIDocument uiDoc, string file, double shortCurve, PdfExplodeStats stats)
	{
		Document doc = uiDoc.Document;
		View view = uiDoc.ActiveView;
		int fileDpi;
		string pixelSize;
		try
		{
			using Bitmap bitmap = RasterExploder.LoadFile(file);
			fileDpi = RasterExploder.DpiOf(bitmap);
			pixelSize = $"{bitmap.Width} × {bitmap.Height} px, {fileDpi} ppp";
		}
		catch (Exception ex)
		{
			TaskDialog.Show(Title, "No se pudo leer la imagen:\n" + ex.Message);
			return false;
		}

		string currentView = ExplodeDwgAvailability.SupportsDetailCurves(view) ? view.Name : null;
		int dpi;
		int scale;
		bool intoCurrent;
		using (var options = new ImageImportDialog(Path.GetFileName(file), fileDpi, pixelSize, currentView))
		{
			if (options.ShowDialog() != DialogResult.OK)
			{
				return false;
			}

			dpi = options.Dpi > 0 ? options.Dpi : fileDpi;
			scale = options.DrawingScale;
			intoCurrent = options.IntoCurrentView;
		}

		View created = null;
		using (var group = new TransactionGroup(doc, "EMASY: importar imagen"))
		{
			group.Start();
			if (intoCurrent)
			{
				// En la vista actual: la esquina inferior izquierda de la imagen en el origen de la vista.
				try
				{
					using Bitmap bitmap = RasterExploder.LoadFile(file);
					if (!RasterExploder.Explode(doc, view, bitmap, view.Origin ?? XYZ.Zero, RasterExploder.FeetPerPixel(dpi, scale), shortCurve, stats))
					{
						stats.Failed.Add(Path.GetFileName(file));
					}
				}
				catch (Exception ex)
				{
					stats.Failed.Add(Path.GetFileName(file) + " (" + ex.Message + ")");
				}
			}
			else
			{
				created = ImageFinder.ImportAsDraftingView(doc, file, dpi, scale, shortCurve, stats);
			}

			group.Assimilate();
		}

		if (created != null)
		{
			try
			{
				uiDoc.ActiveView = created;
			}
			catch (Exception)
			{
				uiDoc.RequestViewChange(created);
			}
		}

		RasterExploder.ShowSummary(stats, "Importar imagen — resumen");
		return true;
	}

	/// <summary>Imágenes raster seleccionadas o, si no hay selección de imágenes, todas las de la vista actual.</summary>
	private static List<ImageInstance> RasterImagesIn(UIDocument uiDoc)
	{
		Document doc = uiDoc.Document;
		List<ImageInstance> selected = uiDoc.Selection.GetElementIds()
			.Select(doc.GetElement)
			.OfType<ImageInstance>()
			.Where(i => RasterExploder.IsRaster(doc.GetElement(i.GetTypeId()) as ImageType))
			.ToList();
		if (selected.Count > 0)
		{
			return selected;
		}

		return new FilteredElementCollector(doc, uiDoc.ActiveView.Id)
			.OfClass(typeof(ImageInstance))
			.Cast<ImageInstance>()
			.Where(i => RasterExploder.IsRaster(doc.GetElement(i.GetTypeId()) as ImageType))
			.ToList();
	}

	/// <summary>Ruta del archivo de un tipo de imagen (absoluta si se puede; puede no existir).</summary>
	internal static string PathOf(Document doc, ImageType type)
	{
		string path;
		try
		{
			path = type?.Path;
		}
		catch (Exception)
		{
			return null;
		}

		if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && !string.IsNullOrEmpty(doc.PathName))
		{
			path = Path.Combine(Path.GetDirectoryName(doc.PathName) ?? string.Empty, path);
		}

		return path;
	}
}

/// <summary>"Explotar Varias Imágenes": la ventana de lista, con las imágenes del modelo.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ImageFinderCommand : IExternalCommand
{
	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
		DwgFinderCommand.Show(commandData.Application, FinderMode.Image);
}
