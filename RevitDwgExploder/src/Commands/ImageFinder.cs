using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Pdf;
using RevitDwgExploder.Raster;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

/// <summary>Lista, explotación en lote e importación de imágenes para "Explotar Varias Imágenes".</summary>
internal static class ImageFinder
{
	public static List<DwgFinderEntry> Collect(Document doc) => PdfFinder.Collect(doc, RasterExploder.IsRaster);

	/// <summary>Explota en lote las imágenes elegidas, cada una en su vista y en la misma posición y tamaño.</summary>
	public static string Explode(UIApplication app, UIDocument uiDoc, List<ElementId> ids, bool deleteOriginals)
	{
		Document doc = uiDoc.Document;
		List<ImageInstance> instances = ids.Select(doc.GetElement).OfType<ImageInstance>().ToList();
		if (instances.Count == 0)
		{
			return "No hay imágenes que explotar (los archivos sin instancias no se pueden explotar).";
		}

		var stats = new PdfExplodeStats();
		var exploded = new List<ElementId>();
		double shortCurve = app.Application.ShortCurveTolerance;

		void OnDialog(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
		{
			stats.SuppressedDialogs++;
			e.OverrideResult((int)TaskDialogResult.Cancel);
		}

		app.DialogBoxShowing += OnDialog;
		try
		{
			foreach (ImageInstance instance in instances)
			{
				var type = doc.GetElement(instance.GetTypeId()) as ImageType;
				string name = type?.Name ?? instance.Id.ToString();
				try
				{
					if (RasterExploder.ExplodeImage(doc, instance, ExplodeImageCommand.PathOf(doc, type), shortCurve, stats))
					{
						exploded.Add(instance.Id);
					}
					else
					{
						stats.Failed.Add(name);
					}
				}
				catch (Exception ex)
				{
					stats.Failed.Add(name + " (" + ex.Message + ")");
				}
			}
		}
		finally
		{
			app.DialogBoxShowing -= OnDialog;
		}

		if (deleteOriginals && exploded.Count > 0)
		{
			DwgFinderHandler.Delete(doc, exploded, deleteFiles: true);
			stats.DeletedOriginals = exploded.Count;
		}

		RasterExploder.ShowSummary(stats, "Explotar Varias Imágenes — resumen");
		return $"{exploded.Count} imagen(es) explotada(s)" + (stats.Failed.Count > 0 ? $"; {stats.Failed.Count} con error." : ".");
	}

	/// <summary>Importa archivos de imagen externos, una vista de dibujo por archivo.</summary>
	public static string ImportFiles(UIApplication app, UIDocument uiDoc, List<string> files, int dpi, int scale)
	{
		Document doc = uiDoc.Document;
		var stats = new PdfExplodeStats();
		var views = new List<View>();
		double shortCurve = app.Application.ShortCurveTolerance;
		using (var group = new TransactionGroup(doc, "EMASY: importar imágenes"))
		{
			group.Start();
			foreach (string file in files.Where(File.Exists))
			{
				View view = ImportAsDraftingView(doc, file, dpi, scale, shortCurve, stats);
				if (view != null)
				{
					views.Add(view);
				}
			}

			group.Assimilate();
		}

		if (views.Count > 0)
		{
			uiDoc.RequestViewChange(views[0]);
		}

		RasterExploder.ShowSummary(stats, "Importar imágenes — resumen");
		return $"{views.Count} imagen(es) importada(s) en vistas de dibujo.";
	}

	/// <summary>Crea una vista de dibujo 1:<paramref name="scale"/> con el nombre del archivo y explota en ella la imagen.</summary>
	public static View ImportAsDraftingView(Document doc, string path, int dpi, int scale, double shortCurveTolerance, PdfExplodeStats stats)
	{
		string label = Path.GetFileName(path);
		ViewFamilyType draftingType = new FilteredElementCollector(doc)
			.OfClass(typeof(ViewFamilyType))
			.Cast<ViewFamilyType>()
			.FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting);
		if (draftingType == null)
		{
			stats.Failed.Add(label + " (el proyecto no tiene tipo de vista de dibujo)");
			return null;
		}

		try
		{
			using Bitmap bitmap = RasterExploder.LoadFile(path);
			int fileDpi = dpi > 0 ? dpi : RasterExploder.DpiOf(bitmap);
			var usedNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
			View view;
			using (var tx = new Transaction(doc, "EMASY: vista de dibujo para imagen"))
			{
				tx.Start();
				view = ViewDrafting.Create(doc, draftingType.Id);
				view.Name = PdfImporter.UniqueName(PdfImporter.MakeValidName(Path.GetFileNameWithoutExtension(path)), usedNames);
				view.Scale = Math.Max(1, scale);
				tx.Commit();
			}

			stats.CreatedViews++;
			if (!RasterExploder.Explode(doc, view, bitmap, XYZ.Zero, RasterExploder.FeetPerPixel(fileDpi, scale), shortCurveTolerance, stats))
			{
				stats.Failed.Add(label);
			}

			return view;
		}
		catch (Exception ex)
		{
			stats.Failed.Add(label + " (" + ex.Message + ")");
			return null;
		}
	}
}
