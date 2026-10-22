using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.Pdf;
using RevitDwgExploder.UI;
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

/// <summary>
/// "Explotar PDF Actual": explota los PDF insertados en la vista actual (o los seleccionados) en su lugar; si no
/// hay ninguno, permite elegir un archivo PDF e importarlo como elementos nativos.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ExplodePdfCommand : IExternalCommand
{
	private const string Title = "Explotar PDF";

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument uiDoc = commandData.Application.ActiveUIDocument;
		Document doc = uiDoc.Document;
		View view = uiDoc.ActiveView;
		double shortCurve = commandData.Application.Application.ShortCurveTolerance;

		List<ImageInstance> targets = PdfImagesIn(uiDoc);
		var stats = new PdfExplodeStats();
		if (targets.Count > 0)
		{
			PdfSources sources = ResolvePaths(doc, targets);
			using (var group = new TransactionGroup(doc, "EMASY: explotar PDF"))
			{
				group.Start();
				foreach (ImageInstance instance in targets)
				{
					ExplodeInstance(doc, instance, sources, shortCurve, stats);
				}

				group.Assimilate();
			}

			PdfImporter.ShowSummary(stats, Title + " — resumen");
			return Result.Succeeded;
		}

		// No hay PDF en la vista: importar un archivo.
		var ask = new TaskDialog(Title)
		{
			MainInstruction = "No hay PDF insertados en la vista actual",
			MainContent = "Puedes elegir un archivo PDF y convertir sus páginas en líneas, rellenos y textos nativos de Revit " +
				"(una vista de dibujo por página, o en la vista actual).",
			CommonButtons = TaskDialogCommonButtons.None
		};
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Elegir un archivo PDF…");
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancelar");
		if (ask.Show() != TaskDialogResult.CommandLink1)
		{
			return Result.Cancelled;
		}

		string file;
		using (var dialog = new OpenFileDialog { Title = "Selecciona el PDF a importar", Filter = "Archivos PDF (*.pdf)|*.pdf", CheckFileExists = true })
		{
			if (dialog.ShowDialog() != DialogResult.OK)
			{
				return Result.Cancelled;
			}

			file = dialog.FileName;
		}

		return ImportFile(uiDoc, file, shortCurve, stats) ? Result.Succeeded : Result.Cancelled;
	}

	/// <summary>Pide páginas/escala/destino e importa el archivo. Devuelve false si el usuario canceló.</summary>
	internal static bool ImportFile(UIDocument uiDoc, string file, double shortCurve, PdfExplodeStats stats)
	{
		Document doc = uiDoc.Document;
		View view = uiDoc.ActiveView;
		int pageCount;
		int detected;
		try
		{
			pageCount = PdfPageReader.CountPages(file);
			detected = PdfExploder.DetectScale(PdfPageReader.Read(file, 1));
		}
		catch (Exception ex)
		{
			TaskDialog.Show(Title, "No se pudo leer el PDF:\n" + ex.Message);
			return false;
		}

		string currentView = ExplodeDwgAvailability.SupportsDetailCurves(view) ? view.Name : null;
		using var options = new PdfImportDialog(Path.GetFileName(file), pageCount, detected, currentView);
		if (options.ShowDialog() != DialogResult.OK)
		{
			return false;
		}

		List<int> pages = options.AllPages ? PdfImporter.ParsePages(null, pageCount) : PdfImporter.ParsePages(options.PageRange, pageCount);
		if (pages.Count == 0)
		{
			TaskDialog.Show(Title, "No se indicó ninguna página válida.");
			return false;
		}

		List<View> created = new List<View>();
		using (var group = new TransactionGroup(doc, "EMASY: importar PDF"))
		{
			group.Start();
			if (options.IntoCurrentView)
			{
				// En la vista actual: la esquina inferior izquierda de la página en el origen de la vista.
				XYZ origin = view.Origin ?? XYZ.Zero;
				if (!PdfImporter.ImportIntoView(doc, view, file, pages[0], options.DrawingScale, origin, shortCurve, stats))
				{
					stats.Failed.Add(Path.GetFileName(file));
				}
			}
			else
			{
				created = PdfImporter.ImportAsDraftingViews(doc, file, pages, options.DrawingScale, shortCurve, stats);
			}

			group.Assimilate();
		}

		if (created.Count > 0)
		{
			try
			{
				uiDoc.ActiveView = created[0];
			}
			catch (Exception)
			{
				uiDoc.RequestViewChange(created[0]);
			}
		}

		PdfImporter.ShowSummary(stats, "Importar PDF — resumen");
		return true;
	}

	/// <summary>PDF seleccionados o, si no hay selección de PDF, todos los de la vista actual.</summary>
	private static List<ImageInstance> PdfImagesIn(UIDocument uiDoc)
	{
		Document doc = uiDoc.Document;
		List<ImageInstance> selected = uiDoc.Selection.GetElementIds()
			.Select(doc.GetElement)
			.OfType<ImageInstance>()
			.Where(i => PdfExploder.IsPdf(doc.GetElement(i.GetTypeId()) as ImageType))
			.ToList();
		if (selected.Count > 0)
		{
			return selected;
		}

		return new FilteredElementCollector(doc, uiDoc.ActiveView.Id)
			.OfClass(typeof(ImageInstance))
			.Cast<ImageInstance>()
			.Where(i => PdfExploder.IsPdf(doc.GetElement(i.GetTypeId()) as ImageType))
			.ToList();
	}

	/// <summary>
	/// Explota un PDF insertado: con su archivo (vectores exactos) o, si no hay archivo, vectorizando la imagen que
	/// Revit guarda del PDF. Devuelve true si se creó algo.
	/// </summary>
	internal static bool ExplodeInstance(Document doc, ImageInstance instance, PdfSources sources, double shortCurve, PdfExplodeStats stats)
	{
		ElementId typeId = instance.GetTypeId();
		string name = doc.GetElement(typeId)?.Name ?? instance.Id.ToString();
		try
		{
			if (sources.Paths.TryGetValue(typeId, out string path))
			{
				try
				{
					if (PdfExploder.ExplodeImage(doc, instance, path, shortCurve, stats))
					{
						return true;
					}
				}
				catch (Exception)
				{
					// El archivo no se pudo leer (dañado, protegido…): se intenta con la imagen que guarda Revit.
				}

				if (Raster.RasterExploder.ExplodeImage(doc, instance, null, shortCurve, stats))
				{
					stats.FromEmbeddedImage++;
					return true;
				}

				stats.Failed.Add(name);
				return false;
			}

			if (sources.UseEmbeddedImage.Contains(typeId))
			{
				if (Raster.RasterExploder.ExplodeImage(doc, instance, null, shortCurve, stats))
				{
					stats.FromEmbeddedImage++;
					return true;
				}

				stats.Failed.Add(name + " (Revit no tiene la imagen del PDF)");
				return false;
			}

			stats.Failed.Add(name + " (archivo no encontrado, omitido)");
			return false;
		}
		catch (Exception ex)
		{
			stats.Failed.Add(name + " (" + ex.Message + ")");
			return false;
		}
	}

	/// <summary>
	/// Archivo del PDF de cada tipo. Si no está donde Revit lo recuerda se busca solo (por nombre, comprobando que sea
	/// el mismo plano); los que no aparecen se resuelven con una sola pregunta: buscarlos a mano, explotar la imagen
	/// que Revit guarda del PDF, u omitirlos.
	/// </summary>
	internal static PdfSources ResolvePaths(Document doc, IEnumerable<ImageInstance> instances)
	{
		var sources = new PdfSources();
		var locator = new PdfFileLocator(doc);
		Dictionary<ElementId, ImageInstance> samples = instances
			.GroupBy(i => i.GetTypeId())
			.ToDictionary(g => g.Key, g => g.First());
		var missing = new List<ElementId>();
		foreach (var pair in samples)
		{
			if (doc.GetElement(pair.Key) is ImageType type && locator.Find(type, pair.Value) is string found)
			{
				sources.Paths[pair.Key] = found;
			}
			else
			{
				missing.Add(pair.Key);
			}
		}

		if (missing.Count == 0)
		{
			return sources;
		}

		List<string> names = missing.Select(id => doc.GetElement(id)?.Name ?? id.ToString()).ToList();
		var ask = new TaskDialog(Title)
		{
			MainInstruction = missing.Count == 1
				? $"No se encuentra el archivo del PDF \"{names[0]}\""
				: $"No se encuentran los archivos de {missing.Count} PDF",
			MainContent = (missing.Count > 1 ? string.Join("\n", names.Take(6).Select(n => "• " + n)) + (names.Count > 6 ? "\n…" : string.Empty) + "\n\n" : string.Empty) +
				"Se buscó en la ruta guardada en Revit, junto al modelo, en Descargas, Escritorio y Documentos. " +
				"El PDF probablemente se vinculó desde otro equipo o carpeta.\n\n" +
				"Sin el archivo se puede explotar la imagen que Revit guarda del PDF (se vectoriza: líneas, rellenos y " +
				"textos por OCR; menos exacto que el PDF original).",
			CommonButtons = TaskDialogCommonButtons.None
		};
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Buscar los archivos…",
			"Elige el PDF; los demás se buscan en la misma carpeta.");
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Explotar la imagen guardada en Revit",
			"No necesita el archivo original.");
		ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Omitir estos PDF");
		TaskDialogResult choice = ask.Show();
		if (choice == TaskDialogResult.CommandLink2)
		{
			sources.UseEmbeddedImage.UnionWith(missing);
			return sources;
		}

		if (choice != TaskDialogResult.CommandLink1)
		{
			return sources;
		}

		// Buscar a mano: después de cada archivo elegido, su carpeta resuelve los demás que estén ahí.
		string lastFolder = null;
		foreach (ElementId typeId in missing)
		{
			if (sources.Paths.ContainsKey(typeId) || !(doc.GetElement(typeId) is ImageType type))
			{
				continue;
			}

			if (lastFolder != null && locator.Find(type, samples[typeId]) is string auto)
			{
				sources.Paths[typeId] = auto;
				continue;
			}

			string stored = PdfFileLocator.StoredPath(doc, type);
			using var dialog = new OpenFileDialog
			{
				Title = $"Selecciona el PDF de \"{type.Name}\"",
				Filter = "Archivos PDF (*.pdf)|*.pdf",
				CheckFileExists = true,
				FileName = SafeFileName(stored) ?? string.Empty,
				InitialDirectory = lastFolder ?? string.Empty
			};
			if (dialog.ShowDialog() != DialogResult.OK)
			{
				continue;
			}

			string file = dialog.FileName;
			if (!PdfFileLocator.Matches(file, type, samples[typeId]))
			{
				var check = new TaskDialog(Title)
				{
					MainInstruction = "El PDF elegido no coincide con el insertado",
					MainContent = $"\"{Path.GetFileName(file)}\" no tiene la página {PdfExploder.PageOf(type)} o sus proporciones son " +
						"distintas a las de la imagen en Revit (puede ser otra versión del plano). ¿Usarlo igual?",
					CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
					DefaultButton = TaskDialogResult.No
				};
				if (check.Show() != TaskDialogResult.Yes)
				{
					continue;
				}
			}

			sources.Paths[typeId] = file;
			PdfFileLocator.Remember(file);
			lastFolder = Path.GetDirectoryName(file);
			locator.AddFolder(lastFolder);
		}

		return sources;
	}

	private static string SafeFileName(string path)
	{
		try
		{
			return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
		}
		catch (Exception)
		{
			return null;
		}
	}
}

/// <summary>De dónde sale cada PDF a explotar: su archivo, o la imagen que Revit guarda de él.</summary>
internal sealed class PdfSources
{
	public Dictionary<ElementId, string> Paths = new Dictionary<ElementId, string>();

	public HashSet<ElementId> UseEmbeddedImage = new HashSet<ElementId>();
}

/// <summary>Disponible siempre que haya un modelo abierto (si no hay PDF en la vista, ofrece importar uno).</summary>
public class ExplodePdfAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
