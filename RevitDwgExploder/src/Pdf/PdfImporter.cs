using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Pdf;

/// <summary>Importa páginas de un archivo PDF como elementos nativos (una vista de dibujo por página, o en una vista dada).</summary>
internal static class PdfImporter
{
	/// <summary>
	/// Crea una vista de dibujo por página (nombre "&lt;archivo&gt; - Pág N", escala 1:<paramref name="scale"/>) y explota
	/// en ella la página a tamaño real: las medidas del papel se multiplican por la escala del dibujo.
	/// </summary>
	public static List<View> ImportAsDraftingViews(Document doc, string path, IList<int> pages, int scale, double shortCurveTolerance, PdfExplodeStats stats)
	{
		var created = new List<View>();
		ViewFamilyType draftingType = new FilteredElementCollector(doc)
			.OfClass(typeof(ViewFamilyType))
			.Cast<ViewFamilyType>()
			.FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting);
		if (draftingType == null)
		{
			stats.Failed.Add(Path.GetFileName(path) + " (el proyecto no tiene tipo de vista de dibujo)");
			return created;
		}

		var usedNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
		string baseName = MakeValidName(Path.GetFileNameWithoutExtension(path));
		foreach (int page in pages)
		{
			string label = $"{Path.GetFileName(path)} pág. {page}";
			try
			{
				PdfDrawing drawing = PdfPageReader.Read(path, page);
				int pageScale = scale > 0 ? scale : Math.Max(1, PdfExploder.DetectScale(drawing));
				if (pageScale <= 1 && scale <= 0)
				{
					pageScale = 100;
				}

				View view;
				using (var tx = new Transaction(doc, "EMASY: vista de dibujo para PDF"))
				{
					tx.Start();
					view = ViewDrafting.Create(doc, draftingType.Id);
					view.Name = UniqueName(pages.Count > 1 ? $"{baseName} - Pág {page}" : baseName, usedNames);
					view.Scale = pageScale;
					tx.Commit();
				}

				stats.CreatedViews++;
				created.Add(view);
				if (!PdfExploder.Explode(doc, view, drawing, XYZ.Zero, PdfExploder.FeetPerPoint * pageScale, shortCurveTolerance, stats))
				{
					stats.Failed.Add(label);
				}
			}
			catch (Exception ex)
			{
				stats.Failed.Add(label + " (" + ex.Message + ")");
			}
		}

		return created;
	}

	/// <summary>Explota una página en una vista existente, con su esquina inferior izquierda en <paramref name="origin"/>.</summary>
	public static bool ImportIntoView(Document doc, View view, string path, int page, int scale, XYZ origin, double shortCurveTolerance, PdfExplodeStats stats)
	{
		PdfDrawing drawing = PdfPageReader.Read(path, page);
		int pageScale = scale > 0 ? scale : Math.Max(1, PdfExploder.DetectScale(drawing));
		return PdfExploder.Explode(doc, view, drawing, origin, PdfExploder.FeetPerPoint * pageScale, shortCurveTolerance, stats);
	}

	/// <summary>"1-3, 5, 8-9" → [1,2,3,5,8,9] (limitado a las páginas del archivo). Vacío o "todas" → todas.</summary>
	public static List<int> ParsePages(string text, int pageCount)
	{
		var pages = new SortedSet<int>();
		if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("todas", StringComparison.OrdinalIgnoreCase))
		{
			for (int i = 1; i <= pageCount; i++)
			{
				pages.Add(i);
			}

			return pages.ToList();
		}

		foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string[] range = part.Split('-');
			if (range.Length == 1 && int.TryParse(range[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int single))
			{
				pages.Add(single);
			}
			else if (range.Length == 2
				&& int.TryParse(range[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
				&& int.TryParse(range[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
			{
				for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
				{
					pages.Add(i);
				}
			}
		}

		return pages.Where(p => p >= 1 && p <= pageCount).ToList();
	}

	internal static string UniqueName(string name, HashSet<string> used)
	{
		string candidate = name;
		for (int i = 2; used.Contains(candidate); i++)
		{
			candidate = $"{name} ({i})";
		}

		used.Add(candidate);
		return candidate;
	}

	internal static string MakeValidName(string name)
	{
		char[] invalid = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' };
		string clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
		return clean.Length == 0 ? "PDF" : clean;
	}

	/// <summary>Resumen de una o varias explotaciones de PDF.</summary>
	public static void ShowSummary(PdfExplodeStats s, string title)
	{
		string text =
			$"Páginas procesadas: {s.Pages}\n" +
			(s.CreatedViews > 0 ? $"Vistas de dibujo creadas: {s.CreatedViews}\n" : string.Empty) +
			$"Líneas: {s.Lines}   Arcos: {s.Arcs}   Curvas: {s.Splines}\n" +
			$"Rellenos (Filled Regions): {s.Fills}" + (s.FailedFills > 0 ? $"  ({s.FailedFills} no se pudieron crear)" : string.Empty) + "\n" +
			$"Textos: {s.Texts}\n" +
			(s.Skipped > 0 ? $"Tramos omitidos (demasiado cortos o inválidos): {s.Skipped}\n" : string.Empty) +
			(s.CreatedStyles > 0 ? $"Line Styles creados (prefijo \"PDF\"): {s.CreatedStyles}\n" : string.Empty) +
			(s.Images > 0 ? $"\nEl PDF contiene {s.Images} imagen(es) raster; no se convierten (no son dibujo vectorial).\n" : string.Empty) +
			(s.Failed.Count > 0 ? $"\nNo se pudieron procesar ({s.Failed.Count}): " + string.Join(", ", s.Failed.Take(8)) + (s.Failed.Count > 8 ? "…" : string.Empty) + "\n" : string.Empty) +
			(s.SuppressedDialogs > 0 ? $"{s.SuppressedDialogs} aviso(s) de Revit se cancelaron automáticamente.\n" : string.Empty) +
			(s.DeletedOriginals > 0 ? $"{s.DeletedOriginals} PDF original(es) eliminado(s) después de explotarlos.\n" : string.Empty) +
			"\nSi el PDF es un escaneo (imagen), no tiene líneas ni textos que convertir.";
		TaskDialog.Show(title, text);
	}
}
