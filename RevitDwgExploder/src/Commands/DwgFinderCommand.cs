using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgExploder.UI;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

/// <summary>Una fila del Buscador DWG's: una instancia de CAD, o un archivo CAD sin instancias en el modelo.</summary>
internal sealed class DwgFinderEntry
{
	public long Id;

	public long TypeId;

	public bool IsInstance;

	public string Name;

	public string Kind;

	public string Location;

	/// <summary>Vista(s) donde se ve el CAD: su vista propia, o todas las vistas donde aparece.</summary>
	public string Views;

	public string Level;

	public string Status;

	public bool Pinned;

	public string Path;
}

internal enum DwgFinderAction
{
	Refresh,
	Select,
	Locate,
	Delete,
	Explode,
	ImportFiles
}

/// <summary>Qué lista la ventana: los CAD (DWG) o los PDF del modelo.</summary>
internal enum FinderMode
{
	Dwg,
	Pdf
}

/// <summary>
/// Ejecuta en el contexto de Revit (vía ExternalEvent) las acciones pedidas desde la ventana del
/// Buscador, que es no modal y no puede tocar el modelo directamente.
/// </summary>
internal sealed class DwgFinderHandler : IExternalEventHandler
{
	public DwgFinderAction Action;

	public FinderMode Mode = FinderMode.Dwg;

	/// <summary>Agregar PDF: archivos a importar, páginas ("" = todas) y escala (0 = detectar en cada PDF).</summary>
	public List<string> FilePaths = new List<string>();

	public int ImportScale;

	public List<long> Ids = new List<long>();

	public bool DeleteFiles;

	/// <summary>Explotar: ajustar la escala de la vista si los textos del DWG son demasiado pequeños.</summary>
	public bool AdjustScale = true;

	/// <summary>Explotar: eliminar los DWG originales una vez explotados.</summary>
	public bool DeleteOriginals;

	/// <summary>Modelo al que pertenece la lista mostrada (las acciones solo se aplican a ese modelo).</summary>
	public string DocumentTitle;

	public Action<List<DwgFinderEntry>, string, string> OnResult;

	public void Execute(UIApplication app)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		if (uiDoc == null)
		{
			OnResult?.Invoke(new List<DwgFinderEntry>(), string.Empty, "No hay ningún modelo abierto.");
			return;
		}

		Document doc = uiDoc.Document;
		string message = null;
		if (Action != DwgFinderAction.Refresh && !string.IsNullOrEmpty(DocumentTitle) && DocumentTitle != doc.Title)
		{
			OnResult?.Invoke(CollectFor(doc), doc.Title, "El modelo activo cambió: la lista se actualizó, vuelve a elegir los archivos.");
			return;
		}

		try
		{
			List<ElementId> ids = Ids.Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
			switch (Action)
			{
				case DwgFinderAction.Select:
					uiDoc.Selection.SetElementIds(ids.Where(id => IsListedInstance(doc.GetElement(id))).ToList());
					message = $"{ids.Count} elemento(s) seleccionado(s).";
					break;
				case DwgFinderAction.Locate:
					message = Locate(uiDoc, ids);
					break;
				case DwgFinderAction.Delete:
					message = Delete(doc, ids, DeleteFiles);
					break;
				case DwgFinderAction.Explode:
					message = Mode == FinderMode.Pdf
						? PdfFinder.Explode(app, uiDoc, ids, DeleteOriginals)
						: Explode(app, uiDoc, ids, AdjustScale, DeleteOriginals);
					break;
				case DwgFinderAction.ImportFiles:
					message = PdfFinder.ImportFiles(app, uiDoc, FilePaths, ImportScale);
					break;
				case DwgFinderAction.Refresh:
					// Redibuja la vista activa para que desaparezca lo eliminado.
					uiDoc.RefreshActiveView();
					break;
			}
		}
		catch (Exception ex)
		{
			message = "No se pudo completar la acción: " + ex.Message;
		}

		OnResult?.Invoke(CollectFor(doc), doc.Title, message);
	}

	public string GetName() => Mode == FinderMode.Pdf ? "EMASY - Explotar Varios PDF's" : "EMASY - Explotar Varios DWG's";

	private List<DwgFinderEntry> CollectFor(Document doc) => Mode == FinderMode.Pdf ? PdfFinder.Collect(doc) : Collect(doc);

	/// <summary>Instancias que lista la ventana (CAD o imágenes PDF).</summary>
	internal static bool IsListedInstance(Element element) => element is ImportInstance || element is ImageInstance;

	/// <summary>
	/// Explota en lote los CAD elegidos. Cada CAD se explota en su vista: la propia si es "solo en su vista";
	/// si es de modelo, la vista activa si lo muestra o, si no, una planta de su nivel. Una transacción por vista.
	/// </summary>
	private static string Explode(UIApplication app, UIDocument uiDoc, List<ElementId> ids, bool adjustScale, bool deleteOriginals)
	{
		Document doc = uiDoc.Document;
		List<ImportInstance> instances = ids.Select(id => doc.GetElement(id)).OfType<ImportInstance>().ToList();
		if (instances.Count == 0)
		{
			return "No hay DWG que explotar (los archivos sin instancias no se pueden explotar).";
		}

		View active = uiDoc.ActiveView;
		var shownInActive = new HashSet<ElementId>();
		try
		{
			shownInActive.UnionWith(new FilteredElementCollector(doc, active.Id).OfClass(typeof(ImportInstance)).ToElementIds());
		}
		catch (Exception)
		{
		}

		var byView = new Dictionary<ElementId, (View View, List<ImportInstance> Items)>();
		var skipped = new List<ImportInstance>();
		foreach (ImportInstance instance in instances)
		{
			View view = null;
			if (instance.OwnerViewId != ElementId.InvalidElementId)
			{
				view = doc.GetElement(instance.OwnerViewId) as View;
			}
			else if (shownInActive.Contains(instance.Id) && ExplodeDwgAvailability.SupportsDetailCurves(active))
			{
				view = active;
			}
			else
			{
				view = FindViewShowing(doc, new List<ElementId> { instance.Id }, ElementId.InvalidElementId);
			}

			if (view == null || !ExplodeDwgAvailability.SupportsDetailCurves(view))
			{
				skipped.Add(instance);
				continue;
			}

			if (!byView.TryGetValue(view.Id, out var group))
			{
				group = (view, new List<ImportInstance>());
				byView[view.Id] = group;
			}

			group.Items.Add(instance);
		}

		if (byView.Count == 0)
		{
			return "Ninguno de los DWG está en una vista donde se puedan crear líneas de detalle.";
		}

		List<ImportInstance> toExplode = byView.Values.SelectMany(g => g.Items).ToList();
		Dictionary<ElementId, string> manualPaths = ExplodeDwgCommand.AskForManualDwgPaths(doc, toExplode);
		var stats = new ExplodeDwgCommand.Stats();
		var exploded = new List<ElementId>();
		double shortCurve = app.Application.ShortCurveTolerance;
		ExplodeDwgCommand.ScalePolicy policy = adjustScale ? ExplodeDwgCommand.ScalePolicy.Adjust : ExplodeDwgCommand.ScalePolicy.Keep;
		var failedViews = new List<string>();
		int suppressedDialogs = 0;

		// Durante el lote, los avisos modales de Revit (p.ej. "contornos demasiado grandes para exportar") se
		// cancelan solos para no detener el proceso esperando al usuario.
		void OnDialog(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
		{
			suppressedDialogs++;
			e.OverrideResult((int)TaskDialogResult.Cancel);
		}

		app.DialogBoxShowing += OnDialog;
		try
		{
			foreach (var (view, items) in byView.Values)
			{
				try
				{
					if (ExplodeDwgCommand.ExplodeInView(doc, view, items, manualPaths, shortCurve, policy, stats))
					{
						stats.Views++;
						exploded.AddRange(items.Select(i => i.Id));
					}
					else
					{
						failedViews.Add(view.Name);
					}
				}
				catch (Exception)
				{
					// Una vista con problemas no detiene el lote.
					failedViews.Add(view.Name);
				}
			}
		}
		finally
		{
			app.DialogBoxShowing -= OnDialog;
		}

		if (deleteOriginals && exploded.Count > 0)
		{
			Delete(doc, exploded, deleteFiles: true);
			stats.DeletedOriginals = exploded.Count;
		}

		stats.FailedViews = failedViews;
		stats.SuppressedDialogs = suppressedDialogs;
		ExplodeDwgCommand.ShowSummary(stats);
		return $"{exploded.Count} DWG explotado(s) en {stats.Views} vista(s)" +
			(failedViews.Count > 0 ? $"; {failedViews.Count} vista(s) con error" : string.Empty) +
			(skipped.Count > 0 ? $"; {skipped.Count} omitido(s) por no estar en una vista 2D." : ".");
	}

	private static string Locate(UIDocument uiDoc, List<ElementId> ids)
	{
		Document doc = uiDoc.Document;
		List<ElementId> instances = ids.Where(id => IsListedInstance(doc.GetElement(id))).ToList();
		if (instances.Count == 0)
		{
			return "Los archivos sin instancias no tienen ubicación en el modelo.";
		}

		// Si el CAD no se ve en la vista activa, se abre una vista donde sí se vea (su vista propia si la tiene).
		ElementId ownerView = doc.GetElement(instances[0]).OwnerViewId;
		bool visibleHere = new FilteredElementCollector(doc, uiDoc.ActiveView.Id).OfClass(typeof(ImportInstance)).ToElementIds().Any(instances.Contains);
		View view = null;
		if (ownerView != ElementId.InvalidElementId && instances.All(id => doc.GetElement(id).OwnerViewId == ownerView))
		{
			view = uiDoc.ActiveView.Id != ownerView ? doc.GetElement(ownerView) as View : null;
		}
		else if (!visibleHere)
		{
			view = FindViewShowing(doc, instances, uiDoc.ActiveView.Id);
		}

		if (view != null)
		{
			try
			{
				uiDoc.ActiveView = view;
			}
			catch (Exception)
			{
				uiDoc.RequestViewChange(view);
			}
		}

		uiDoc.Selection.SetElementIds(instances);
		uiDoc.ShowElements(instances);
		return $"{instances.Count} elemento(s) ubicado(s) y seleccionado(s).";
	}

	internal static string Delete(Document doc, List<ElementId> ids, bool deleteFiles)
	{
		if (ids.Count == 0)
		{
			return "No hay nada que eliminar.";
		}

		var typeIds = new HashSet<ElementId>();
		int deleted = 0;
		using (var tx = new Transaction(doc, "EMASY: eliminar DWG"))
		{
			tx.Start();
			var toDelete = new List<ElementId>();
			foreach (ElementId id in ids)
			{
				Element element = doc.GetElement(id);
				if (IsListedInstance(element))
				{
					typeIds.Add(element.GetTypeId());
					if (element.Pinned)
					{
						element.Pinned = false;
					}

					toDelete.Add(id);
				}
				else if (element is ElementType)
				{
					// Fila de un archivo sin instancias: se elimina el archivo (tipo/vínculo) directamente.
					toDelete.Add(id);
				}
			}

			if (toDelete.Count > 0)
			{
				doc.Delete(toDelete);
				deleted = toDelete.Count;
			}

			if (deleteFiles)
			{
				var remainingTypes = new HashSet<ElementId>(new FilteredElementCollector(doc)
					.OfClass(typeof(ImportInstance))
					.Select(e => e.GetTypeId())
					.Concat(new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Select(e => e.GetTypeId())));
				List<ElementId> orphanTypes = typeIds.Where(t => doc.GetElement(t) != null && !remainingTypes.Contains(t)).ToList();
				if (orphanTypes.Count > 0)
				{
					doc.Delete(orphanTypes);
				}
			}

			tx.Commit();
		}

		return $"{deleted} elemento(s) eliminado(s)" + (deleteFiles ? " (y su archivo DWG del proyecto si no le quedaban más instancias)." : ".");
	}

	/// <summary>Todas las instancias de CAD del modelo y los archivos CAD que ya no tienen instancias.</summary>
	public static List<DwgFinderEntry> Collect(Document doc)
	{
		var result = new List<DwgFinderEntry>();
		var usedTypes = new HashSet<long>();
		List<ImportInstance> instances = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
		Dictionary<ElementId, List<string>> viewsByInstance = ViewsByInstance(doc, instances);
		foreach (ImportInstance instance in instances)
		{
			Element type = doc.GetElement(instance.GetTypeId());
			usedTypes.Add(instance.GetTypeId().Value);
			result.Add(new DwgFinderEntry
			{
				Id = instance.Id.Value,
				TypeId = instance.GetTypeId().Value,
				IsInstance = true,
				Name = type?.Name ?? instance.Name,
				Kind = instance.IsLinked ? "Vinculado" : "Importado",
				Location = LocationOf(doc, instance),
				Views = viewsByInstance.TryGetValue(instance.Id, out List<string> views) && views.Count > 0
					? string.Join(", ", views)
					: "(sin planta en su nivel)",
				Level = LevelOf(doc, instance),
				Status = StatusOf(type, instance.IsLinked),
				Pinned = instance.Pinned,
				Path = PathOf(type)
			});
		}

		foreach (CADLinkType type in new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>())
		{
			if (usedTypes.Contains(type.Id.Value))
			{
				continue;
			}

			bool linked = type.IsExternalFileReference();
			result.Add(new DwgFinderEntry
			{
				Id = type.Id.Value,
				TypeId = type.Id.Value,
				IsInstance = false,
				Name = type.Name,
				Kind = (linked ? "Vinculado" : "Importado") + " (sin instancias)",
				Location = "—",
				Views = "—",
				Level = "—",
				Status = StatusOf(type, linked),
				Pinned = false,
				Path = PathOf(type)
			});
		}

		return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static string LocationOf(Document doc, ImportInstance instance) =>
		instance.OwnerViewId != ElementId.InvalidElementId ? "Solo en su vista" : "Modelo";

	/// <summary>
	/// Vistas de cada CAD, sin calcular visibilidades (eso obligaba a Revit a procesar cada vista del modelo y
	/// era lo que hacía lento al buscador): un CAD "solo vista actual" tiene su vista propia, y uno de modelo se
	/// muestra en las plantas de su nivel. Solo se leen datos de las vistas, así que es casi instantáneo.
	/// </summary>
	private static Dictionary<ElementId, List<string>> ViewsByInstance(Document doc, List<ImportInstance> instances)
	{
		var result = new Dictionary<ElementId, List<string>>();
		if (instances.Count == 0)
		{
			return result;
		}

		Dictionary<ElementId, List<ViewPlan>> plansByLevel = PlansByLevel(doc);
		foreach (ImportInstance instance in instances)
		{
			var names = new List<string>();
			if (instance.OwnerViewId != ElementId.InvalidElementId)
			{
				if (doc.GetElement(instance.OwnerViewId) is View owner)
				{
					names.Add(owner.Name);
				}
			}
			else if (plansByLevel.TryGetValue(LevelIdOf(instance), out List<ViewPlan> plans))
			{
				names.AddRange(plans.Select(p => p.Name));
			}

			result[instance.Id] = names;
		}

		return result;
	}

	/// <summary>Plantas (no plantillas) agrupadas por su nivel, ordenadas por nombre.</summary>
	private static Dictionary<ElementId, List<ViewPlan>> PlansByLevel(Document doc)
	{
		return new FilteredElementCollector(doc)
			.OfClass(typeof(ViewPlan))
			.Cast<ViewPlan>()
			.Where(v => !v.IsTemplate && v.GenLevel != null)
			.GroupBy(v => v.GenLevel.Id)
			.ToDictionary(
				g => g.Key,
				g => g.OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
	}

	private static ElementId LevelIdOf(ImportInstance instance)
	{
		try
		{
			ElementId levelId = instance.LevelId;
			if (levelId == ElementId.InvalidElementId)
			{
				levelId = instance.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId;
			}

			return levelId;
		}
		catch (Exception)
		{
			return ElementId.InvalidElementId;
		}
	}

	/// <summary>
	/// Una vista (distinta de la activa) donde se vean los CAD: se prueban solo las plantas de su nivel,
	/// comprobando la visibilidad de pocas vistas en lugar de recorrer todo el modelo.
	/// </summary>
	private static View FindViewShowing(Document doc, List<ElementId> ids, ElementId skip)
	{
		var wanted = new HashSet<ElementId>(ids);
		Dictionary<ElementId, List<ViewPlan>> plansByLevel = PlansByLevel(doc);
		List<ViewPlan> candidates = ids
			.Select(id => doc.GetElement(id) as ImportInstance)
			.Where(i => i != null)
			.SelectMany(i => plansByLevel.TryGetValue(LevelIdOf(i), out List<ViewPlan> plans) ? plans : new List<ViewPlan>())
			.Where(v => v.Id != skip)
			.Distinct()
			.ToList();
		foreach (ViewPlan view in candidates)
		{
			try
			{
				if (new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).ToElementIds().Any(wanted.Contains))
				{
					return view;
				}
			}
			catch (Exception)
			{
			}
		}

		// Si ninguna comprobación confirmó la visibilidad (p.ej. el CAD está oculto), la primera planta de su nivel.
		return candidates.FirstOrDefault();
	}

	private static string LevelOf(Document doc, ImportInstance instance)
	{
		try
		{
			ElementId levelId = instance.LevelId;
			if (levelId == ElementId.InvalidElementId)
			{
				levelId = instance.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId;
			}

			return doc.GetElement(levelId)?.Name ?? "—";
		}
		catch (Exception)
		{
			return "—";
		}
	}

	private static string StatusOf(Element type, bool linked)
	{
		if (!linked)
		{
			return "Incrustado";
		}

		try
		{
			ExternalFileReference reference = type?.GetExternalFileReference();
			return reference?.GetLinkedFileStatus() switch
			{
				LinkedFileStatus.Loaded => "Cargado",
				LinkedFileStatus.NotFound => "No encontrado",
				LinkedFileStatus.Unloaded => "Descargado",
				LinkedFileStatus.Invalid => "Inválido",
				null => "—",
				var other => other.ToString()
			};
		}
		catch (Exception)
		{
			return "—";
		}
	}

	private static string PathOf(Element type)
	{
		try
		{
			ExternalFileReference reference = type?.GetExternalFileReference();
			return reference == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}
}

/// <summary>Abre (o trae al frente) la ventana del Buscador DWG's.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class DwgFinderCommand : IExternalCommand
{
	private static readonly Dictionary<FinderMode, DwgFinderForm> Forms = new Dictionary<FinderMode, DwgFinderForm>();

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
		Show(commandData.Application, FinderMode.Dwg);

	/// <summary>Abre (o trae al frente) la ventana de lista en el modo indicado.</summary>
	internal static Result Show(UIApplication app, FinderMode mode)
	{
		Document doc = app.ActiveUIDocument?.Document;
		if (doc == null)
		{
			return Result.Cancelled;
		}

		if (Forms.TryGetValue(mode, out DwgFinderForm existing) && !existing.IsDisposed)
		{
			existing.Activate();
			existing.RequestRefresh();
			return Result.Succeeded;
		}

		var handler = new DwgFinderHandler { Mode = mode };
		var externalEvent = ExternalEvent.Create(handler);
		var form = new DwgFinderForm(handler, externalEvent, mode);
		form.SetEntries(mode == FinderMode.Pdf ? PdfFinder.Collect(doc) : DwgFinderHandler.Collect(doc), doc.Title, null);
		form.Show(new WindowHandle(app.MainWindowHandle));
		Forms[mode] = form;
		return Result.Succeeded;
	}

	private sealed class WindowHandle : System.Windows.Forms.IWin32Window
	{
		public WindowHandle(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }
	}
}

/// <summary>"Explotar Varios PDF's": la misma ventana de lista, con los PDF del modelo.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class PdfFinderCommand : IExternalCommand
{
	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
		DwgFinderCommand.Show(commandData.Application, FinderMode.Pdf);
}

/// <summary>El Buscador está disponible siempre que haya un modelo abierto.</summary>
public class DwgFinderAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
