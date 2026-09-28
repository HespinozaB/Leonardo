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
			Dictionary<ElementId, string> paths = ResolvePaths(doc, targets);
			using (var group = new TransactionGroup(doc, "EMASY: explotar PDF"))
			{
				group.Start();
				foreach (ImageInstance instance in targets)
				{
					string name = doc.GetElement(instance.GetTypeId())?.Name ?? instance.Id.ToString();
					if (!paths.TryGetValue(instance.GetTypeId(), out string path))
					{
						stats.Failed.Add(name + " (archivo no encontrado)");
						continue;
					}

					try
					{
						if (!PdfExploder.ExplodeImage(doc, instance, path, shortCurve, stats))
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

	/// <summary>Ruta del PDF de cada tipo; si el archivo ya no está donde Revit lo recuerda, se pide al usuario.</summary>
	internal static Dictionary<ElementId, string> ResolvePaths(Document doc, IEnumerable<ImageInstance> instances)
	{
		var result = new Dictionary<ElementId, string>();
		foreach (ElementId typeId in instances.Select(i => i.GetTypeId()).Distinct())
		{
			var type = doc.GetElement(typeId) as ImageType;
			string path = type?.Path;
			if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && !string.IsNullOrEmpty(doc.PathName))
			{
				path = Path.Combine(Path.GetDirectoryName(doc.PathName) ?? string.Empty, path);
			}

			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				using var dialog = new OpenFileDialog
				{
					Title = $"No se encuentra el PDF de \"{type?.Name}\": selecciona el archivo",
					Filter = "Archivos PDF (*.pdf)|*.pdf",
					CheckFileExists = true,
					FileName = Path.GetFileName(path ?? string.Empty)
				};
				path = dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
			}

			if (path != null)
			{
				result[typeId] = path;
			}
		}

		return result;
	}
}

/// <summary>Disponible siempre que haya un modelo abierto (si no hay PDF en la vista, ofrece importar uno).</summary>
public class ExplodePdfAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
