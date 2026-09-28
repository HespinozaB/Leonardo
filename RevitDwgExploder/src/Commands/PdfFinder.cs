using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Pdf;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

/// <summary>Lista, explotación en lote e importación de PDF para la ventana "Explotar Varios PDF's".</summary>
internal static class PdfFinder
{
	/// <summary>PDF insertados en el modelo (imágenes cuyo archivo es un PDF) y PDF que quedaron sin instancias.</summary>
	public static List<DwgFinderEntry> Collect(Document doc)
	{
		var result = new List<DwgFinderEntry>();
		var usedTypes = new HashSet<long>();
		foreach (ImageInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>())
		{
			var type = doc.GetElement(instance.GetTypeId()) as ImageType;
			if (!PdfExploder.IsPdf(type))
			{
				continue;
			}

			usedTypes.Add(type.Id.Value);
			result.Add(new DwgFinderEntry
			{
				Id = instance.Id.Value,
				TypeId = type.Id.Value,
				IsInstance = true,
				Name = NameOf(type),
				Kind = KindOf(type),
				Location = "Solo en su vista",
				Views = (doc.GetElement(instance.OwnerViewId) as View)?.Name ?? "—",
				Level = "—",
				Status = StatusOf(doc, type),
				Pinned = instance.Pinned,
				Path = type.Path ?? string.Empty
			});
		}

		foreach (ImageType type in new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>())
		{
			if (!PdfExploder.IsPdf(type) || usedTypes.Contains(type.Id.Value))
			{
				continue;
			}

			result.Add(new DwgFinderEntry
			{
				Id = type.Id.Value,
				TypeId = type.Id.Value,
				IsInstance = false,
				Name = NameOf(type),
				Kind = KindOf(type) + " (sin instancias)",
				Location = "—",
				Views = "—",
				Level = "—",
				Status = StatusOf(doc, type),
				Pinned = false,
				Path = type.Path ?? string.Empty
			});
		}

		return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static string NameOf(ImageType type)
	{
		int page = PdfExploder.PageOf(type);
		return page > 1 ? $"{type.Name} (pág. {page})" : type.Name;
	}

	private static string KindOf(ImageType type)
	{
		try
		{
			return type.Source == ImageTypeSource.Link ? "Vinculado" : "Importado";
		}
		catch (Exception)
		{
			return "Importado";
		}
	}

	private static string StatusOf(Document doc, ImageType type)
	{
		string path = type.Path;
		if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && !string.IsNullOrEmpty(doc.PathName))
		{
			path = Path.Combine(Path.GetDirectoryName(doc.PathName) ?? string.Empty, path);
		}

		return !string.IsNullOrEmpty(path) && File.Exists(path) ? "Archivo encontrado" : "No encontrado";
	}

	/// <summary>Explota en lote los PDF elegidos, cada uno en su vista y en la misma posición y tamaño.</summary>
	public static string Explode(UIApplication app, UIDocument uiDoc, List<ElementId> ids, bool deleteOriginals)
	{
		Document doc = uiDoc.Document;
		List<ImageInstance> instances = ids.Select(doc.GetElement).OfType<ImageInstance>().ToList();
		if (instances.Count == 0)
		{
			return "No hay PDF que explotar (los archivos sin instancias no se pueden explotar).";
		}

		Dictionary<ElementId, string> paths = ExplodePdfCommand.ResolvePaths(doc, instances);
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
				string name = doc.GetElement(instance.GetTypeId())?.Name ?? instance.Id.ToString();
				if (!paths.TryGetValue(instance.GetTypeId(), out string path))
				{
					stats.Failed.Add(name + " (archivo no encontrado)");
					continue;
				}

				try
				{
					if (PdfExploder.ExplodeImage(doc, instance, path, shortCurve, stats))
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

		PdfImporter.ShowSummary(stats, "Explotar Varios PDF's — resumen");
		return $"{exploded.Count} PDF explotado(s)" + (stats.Failed.Count > 0 ? $"; {stats.Failed.Count} con error." : ".");
	}

	/// <summary>Importa archivos PDF externos: todas sus páginas, una vista de dibujo por página.</summary>
	public static string ImportFiles(UIApplication app, UIDocument uiDoc, List<string> files, int scale)
	{
		Document doc = uiDoc.Document;
		var stats = new PdfExplodeStats();
		var views = new List<View>();
		double shortCurve = app.Application.ShortCurveTolerance;
		using (var group = new TransactionGroup(doc, "EMASY: importar PDF"))
		{
			group.Start();
			foreach (string file in files.Where(File.Exists))
			{
				try
				{
					int pageCount = PdfPageReader.CountPages(file);
					views.AddRange(PdfImporter.ImportAsDraftingViews(doc, file, PdfImporter.ParsePages(null, pageCount), scale, shortCurve, stats));
				}
				catch (Exception ex)
				{
					stats.Failed.Add(Path.GetFileName(file) + " (" + ex.Message + ")");
				}
			}

			group.Assimilate();
		}

		if (views.Count > 0)
		{
			uiDoc.RequestViewChange(views[0]);
		}

		PdfImporter.ShowSummary(stats, "Importar PDF — resumen");
		return $"{files.Count} PDF importado(s) en {stats.CreatedViews} vista(s) de dibujo.";
	}
}
