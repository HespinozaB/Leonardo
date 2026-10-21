using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamFill.Core;
using RevitParamFill.UI;

namespace RevitParamFill.Commands;

internal enum FillAction
{
	Prepare,
	ReadSectors,
	ListLayers,
	Compute,
	CreateAndCompute,
	Write,
	CreateParameters,
	Select,
	ReadSelection
}

/// <summary>Lo que ofrece el modelo para configurar las reglas y los sectores.</summary>
internal sealed class ModelCatalog
{
	public List<string> Parameters = new List<string>();

	public List<(long Id, string Name, string Level)> Views = new List<(long, string, string)>();

	public List<string> RegionTypes = new List<string>();

	public List<string> LineStyles = new List<string>();

	/// <summary>DWG en el modelo: id, nombre, vista donde está (o -1 si es de modelo), vinculado.</summary>
	public List<(long Id, string Name, long ViewId, bool Linked)> Dwgs = new List<(long, string, long, bool)>();
}

/// <summary>Ejecuta las acciones de la ventana en el contexto válido de la API de Revit (ExternalEvent).</summary>
internal sealed class FillHandler : IExternalEventHandler
{
	public FillAction Action;

	public RuleSet Rules = new RuleSet();

	public int SectorIndex;

	public List<(long ElementId, string Target, string Value)> Values = new List<(long, string, string)>();

	public List<string> Names = new List<string>();

	public List<long> Ids = new List<long>();

	public string DocumentTitle;

	public Action<ModelCatalog, string> OnCatalog;

	public Action<int, List<Polygon>, string> OnSectors;

	public Action<int, List<string>> OnLayers;

	public Action<FillPlan, string> OnPlan;

	public Action<List<long>> OnSelection;

	public Action<string, bool> OnMessage;

	public void Execute(UIApplication app)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		Document doc = uiDoc?.Document;
		if (doc == null)
		{
			OnMessage?.Invoke("No hay ningún modelo abierto.", false);
			return;
		}

		try
		{
			switch (Action)
			{
				case FillAction.Prepare:
					OnCatalog?.Invoke(Catalog(doc), doc.Title);
					break;
				case FillAction.ReadSectors:
				{
					SectorSet set = Rules.SectorSets[SectorIndex];
					string message = SectorReader.Read(doc, set);
					OnSectors?.Invoke(SectorIndex, set.Sectors, message);
					break;
				}
				case FillAction.ListLayers:
				{
					SectorSet set = Rules.SectorSets[SectorIndex];
					var instance = doc.GetElement(new ElementId(set.DwgInstanceId)) as ImportInstance;
					var view = doc.GetElement(new ElementId(set.ViewId)) as View;
					OnLayers?.Invoke(SectorIndex, instance == null ? new List<string>() : SectorReader.DwgLayers(doc, instance, view));
					break;
				}
				case FillAction.Compute:
					OnPlan?.Invoke(Filler.Compute(doc, Rules), doc.Title);
					break;
				case FillAction.CreateAndCompute:
				{
					// Crea los parámetros que faltan (si los hay) y calcula el contenido.
					string created = null;
					if (Names.Count > 0)
					{
						created = Run(doc, "EMASY: crear parámetros", () => SharedParams.Ensure(doc, Names));
						Names = new List<string>();
						OnCatalog?.Invoke(Catalog(doc), doc.Title);
					}

					FillPlan plan = Filler.Compute(doc, Rules);
					if (created != null)
					{
						plan.Notes.Insert(0, created);
					}

					OnPlan?.Invoke(plan, doc.Title);
					break;
				}
				case FillAction.Write:
					if (!string.IsNullOrEmpty(DocumentTitle) && DocumentTitle != doc.Title)
					{
						OnMessage?.Invoke("El modelo activo cambió: vuelve a calcular antes de escribir.", false);
						break;
					}

					OnMessage?.Invoke(Run(doc, "EMASY: rellenar parámetros", () => Filler.Write(doc, Values)), true);
					break;
				case FillAction.CreateParameters:
					OnMessage?.Invoke(Run(doc, "EMASY: crear parámetros", () => SharedParams.Ensure(doc, Names)), true);
					OnCatalog?.Invoke(Catalog(doc), doc.Title);
					break;
				case FillAction.Select:
				{
					var ids = Ids.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList();
					uiDoc.Selection.SetElementIds(ids);
					if (ids.Count > 0)
					{
						try
						{
							uiDoc.ShowElements(ids);
						}
						catch (Exception)
						{
						}
					}

					OnMessage?.Invoke($"{ids.Count} elemento(s) seleccionados en Revit.", false);
					break;
				}
				case FillAction.ReadSelection:
					OnSelection?.Invoke(uiDoc.Selection.GetElementIds().Select(id => id.Value).ToList());
					break;
			}
		}
		catch (Exception ex)
		{
			OnMessage?.Invoke("Error: " + ex.Message, false);
		}
	}

	public string GetName() => "EMASY · Rellenar parámetros";

	private static ModelCatalog Catalog(Document doc)
	{
		var catalog = new ModelCatalog();
		var names = new HashSet<string>(StringComparer.Ordinal);
		DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
		while (iterator.MoveNext())
		{
			if (iterator.Key?.Name != null)
			{
				names.Add(iterator.Key.Name);
			}
		}

		// También los parámetros de familia (no están en el proyecto): se toman de una muestra de elementos y sus tipos.
		var seenTypes = new HashSet<long>();
		int sampled = 0;
		foreach (Element element in ModelReader.ModelElements(doc))
		{
			if (++sampled > 4000)
			{
				break;
			}

			var owners = new List<Element> { element };
			ElementId typeId = element.GetTypeId();
			if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId.Value) && doc.GetElement(typeId) is Element type)
			{
				owners.Add(type);
			}

			foreach (Element owner in owners)
			{
				foreach (Parameter parameter in owner.Parameters)
				{
					if (parameter.Definition != null && !parameter.IsReadOnly && parameter.StorageType != StorageType.ElementId &&
						parameter.StorageType != StorageType.None)
					{
						names.Add(parameter.Definition.Name);
					}
				}
			}
		}

		catalog.Parameters = names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		catalog.Views = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
			.Where(v => !v.IsTemplate).Select(v => (v.Id.Value, $"{v.ViewType}: {v.Name}", v.GenLevel?.Name ?? string.Empty))
			.OrderBy(v => v.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
		catalog.RegionTypes = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Select(t => t.Name)
			.Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
		if (lines != null)
		{
			catalog.LineStyles = lines.SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		}

		catalog.Dwgs = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
			.Select(i => (i.Id.Value, doc.GetElement(i.GetTypeId())?.Name ?? i.Name, i.OwnerViewId.Value, i.IsLinked))
			.ToList();
		return catalog;
	}

	private static string Run(Document doc, string name, Func<string> body)
	{
		using var transaction = new Transaction(doc, name);
		// Los avisos de miles de elementos modificados ralentizan la confirmación y abren diálogos.
		FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
		options.SetFailuresPreprocessor(new WarningSwallower());
		options.SetForcedModalHandling(false);
		transaction.SetFailureHandlingOptions(options);
		transaction.Start();
		string message = body();
		return transaction.Commit() == TransactionStatus.Committed ? message : "Revit rechazó la transacción: no se guardaron cambios.";
	}

	/// <summary>Borra las advertencias (no los errores) al confirmar.</summary>
	private sealed class WarningSwallower : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
		{
			foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
			{
				if (failure.GetSeverity() == FailureSeverity.Warning)
				{
					accessor.DeleteWarning(failure);
				}
			}

			return FailureProcessingResult.Continue;
		}
	}
}

/// <summary>Abre (o trae al frente) la ventana de rellenar parámetros.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class FillCommand : IExternalCommand
{
	private static FillForm _form;

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIApplication app = commandData.Application;
		if (app.ActiveUIDocument?.Document == null)
		{
			return Result.Cancelled;
		}

		if (_form != null && !_form.IsDisposed)
		{
			_form.Activate();
			return Result.Succeeded;
		}

		var handler = new FillHandler();
		_form = new FillForm(handler, ExternalEvent.Create(handler));
		_form.Show(new WindowHandle(app.MainWindowHandle));
		_form.Start();
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

/// <summary>Disponible siempre que haya un modelo abierto.</summary>
public class FillAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
