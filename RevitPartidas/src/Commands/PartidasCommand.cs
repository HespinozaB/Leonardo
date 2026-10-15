using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPartidas.Core;
using RevitPartidas.UI;

namespace RevitPartidas.Commands;

internal enum PartidasAction
{
	Scan,
	SuggestLinks,
	LinkValues,
	LoadAssemblyCodes,
	ParameterStatus,
	CreateParameters,
	Write,
	Select,
	ReadSelection,
	Show3D,
	Pick3D,
	Reset3D
}

/// <summary>Ejecuta las acciones de la ventana en el contexto válido de la API de Revit (ExternalEvent).</summary>
internal sealed class PartidasHandler : IExternalEventHandler
{
	public PartidasAction Action;

	public Budget Budget;

	public PartidasConfig Config = new PartidasConfig();

	/// <summary>Para Write: elemento → valores de los niveles (null = vaciar).</summary>
	public Dictionary<long, string[]> Assignments = new Dictionary<long, string[]>();

	/// <summary>Para Write: tipo → nuevo Código de montaje.</summary>
	public Dictionary<long, string> TypeCodes = new Dictionary<long, string>();

	/// <summary>Para Select y para los elementos en conflicto de Show3D.</summary>
	public List<long> Ids = new List<long>();

	public List<Review3DOption> Options = new List<Review3DOption>();

	/// <summary>Para Write: después de escribir, quitar los colores de la vista de revisión 3D.</summary>
	public bool Reset3DAfterWrite;

	/// <summary>Modelo al que pertenece la lista mostrada (las escrituras solo se aplican a ese modelo).</summary>
	public string DocumentTitle;

	public Action<ScanResult, string, string> OnScan;

	/// <summary>Elementos escritos y tipos con nuevo código de montaje (para actualizar la lista sin volver a analizar) y mensaje.</summary>
	public Action<Dictionary<long, string[]>, Dictionary<long, string>, string> OnWritten;

	public Action<Dictionary<int, Dictionary<string, int>>> OnLinkValues;

	public Action<List<long>> OnSelection;

	public Action<List<LinkSuggestion>> OnSuggestions;

	public Action<bool[], string> OnParameterStatus;

	public Action<long?> OnPicked;

	/// <summary>Mensaje simple (errores, avisos) para la barra de estado.</summary>
	public Action<string> OnMessage;

	public void Execute(UIApplication app)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		Document doc = uiDoc?.Document;
		if (doc == null)
		{
			OnMessage?.Invoke("No hay ningún modelo abierto.");
			return;
		}

		try
		{
			bool sameModel = string.IsNullOrEmpty(DocumentTitle) || DocumentTitle == doc.Title;
			switch (Action)
			{
				case PartidasAction.Scan:
					OnScan?.Invoke(ElementScanner.Run(doc, Budget, Config), doc.Title, null);
					break;
				case PartidasAction.SuggestLinks:
					OnSuggestions?.Invoke(ElementScanner.SuggestLinks(doc, Budget, Config));
					break;
				case PartidasAction.LinkValues:
					OnLinkValues?.Invoke(ElementScanner.LinkValueCounts(doc, Config));
					break;
				case PartidasAction.LoadAssemblyCodes:
				{
					OnMessage?.Invoke(Run(doc, "EMASY: cargar códigos de montaje", () => AssemblyCodes.Generate(doc, Budget, Config.Levels)));
					break;
				}
				case PartidasAction.ParameterStatus:
					OnParameterStatus?.Invoke(SharedParams.FindBound(doc, Config.Names).Select(d => d != null).ToArray(), null);
					break;
				case PartidasAction.CreateParameters:
				{
					string message = Run(doc, "EMASY: crear parámetros de partida", () => SharedParams.Ensure(doc, Config.Names));
					OnParameterStatus?.Invoke(SharedParams.FindBound(doc, Config.Names).Select(d => d != null).ToArray(), message);
					break;
				}
				case PartidasAction.Write:
					if (!sameModel)
					{
						OnMessage?.Invoke("El modelo activo cambió: vuelve a analizar antes de escribir.");
						break;
					}

					Write(doc);
					break;
				case PartidasAction.Select:
					Select(uiDoc);
					break;
				case PartidasAction.ReadSelection:
					OnSelection?.Invoke(uiDoc.Selection.GetElementIds().Select(id => id.Value).ToList());
					break;
				case PartidasAction.Show3D:
					OnMessage?.Invoke(Review3D.Show(uiDoc, Ids, Options));
					break;
				case PartidasAction.Pick3D:
					OnPicked?.Invoke(Review3D.Pick(uiDoc));
					break;
				case PartidasAction.Reset3D:
					Review3D.Reset(doc);
					OnMessage?.Invoke("Se quitaron los colores de la vista de revisión.");
					break;
			}
		}
		catch (Exception ex)
		{
			OnMessage?.Invoke("Error: " + ex.Message);
		}
	}

	public string GetName() => "EMASY · Partidas";

	private static string Run(Document doc, string name, Func<string> body)
	{
		using var transaction = new Transaction(doc, name);
		transaction.Start();
		string message = body();
		return transaction.Commit() == TransactionStatus.Committed ? message : "Revit rechazó la transacción: no se guardaron cambios.";
	}

	private void Write(Document doc)
	{
		string[] names = Config.Names;
		Guid[] guids = names.Select(SharedParams.GuidFor).ToArray();
		var written = new Dictionary<long, string[]>();
		int missing = 0;
		int failed = 0;
		var typesWritten = new Dictionary<long, string>();
		int typesFailed = 0;
		using (var transaction = new Transaction(doc, "EMASY: escribir partidas"))
		{
			transaction.Start();
			foreach (KeyValuePair<long, string> typeCode in TypeCodes)
			{
				Parameter parameter = AssemblyCodes.CodeParameter(doc.GetElement(new ElementId(typeCode.Key)));
				try
				{
					if (parameter != null && !parameter.IsReadOnly && parameter.Set(typeCode.Value ?? string.Empty))
					{
						typesWritten[typeCode.Key] = typeCode.Value ?? string.Empty;
					}
					else
					{
						typesFailed++;
					}
				}
				catch (Exception)
				{
					typesFailed++;
				}
			}

			foreach (KeyValuePair<long, string[]> assignment in Assignments)
			{
				Element element = doc.GetElement(new ElementId(assignment.Key));
				if (element == null)
				{
					failed++;
					continue;
				}

				Parameter[] parameters = SharedParams.Get(element, names, guids);
				if (parameters.Any(p => p == null))
				{
					missing++;
					continue;
				}

				string[] levels = assignment.Value ?? new string[names.Length];
				bool ok = true;
				for (int i = 0; i < parameters.Length; i++)
				{
					string value = i < levels.Length ? levels[i] ?? string.Empty : string.Empty;
					try
					{
						if ((parameters[i].AsString() ?? string.Empty) != value)
						{
							ok &= !parameters[i].IsReadOnly && parameters[i].Set(value);
						}
					}
					catch (Exception)
					{
						ok = false;
					}
				}

				if (ok)
				{
					written[assignment.Key] = parameters.Select(p => p.AsString() ?? string.Empty).ToArray();
				}
				else
				{
					failed++;
				}
			}

			if (transaction.Commit() != TransactionStatus.Committed)
			{
				OnMessage?.Invoke("Revit rechazó la transacción: no se guardaron cambios.");
				return;
			}
		}

		string text = $"{written.Count} elemento(s) actualizado(s)." + (TypeCodes.Count > 0 ? $" Código de montaje en {typesWritten.Count} tipo(s)." : string.Empty);
		if (typesFailed > 0)
		{
			text += $" {typesFailed} tipo(s) sin código de montaje editable.";
		}

		if (missing > 0)
		{
			text += $" {missing} sin los parámetros (créalos en el paso 2).";
		}

		if (failed > 0)
		{
			text += $" {failed} no se pudieron escribir (de solo lectura, en un grupo o vínculo).";
		}

		if (Reset3DAfterWrite)
		{
			Reset3DAfterWrite = false;
			Review3D.Reset(doc);
		}

		TypeCodes = new Dictionary<long, string>();
		OnWritten?.Invoke(written, typesWritten, text);
	}

	private void Select(UIDocument uiDoc)
	{
		var ids = Ids.Select(id => new ElementId(id)).Where(id => uiDoc.Document.GetElement(id) != null).ToList();
		uiDoc.Selection.SetElementIds(ids);
		if (ids.Count > 0)
		{
			try
			{
				uiDoc.ShowElements(ids);
			}
			catch (Exception)
			{
				// Elementos que no se ven en la vista activa: quedan seleccionados igualmente.
			}
		}

		OnMessage?.Invoke($"{ids.Count} elemento(s) seleccionados en Revit.");
	}
}

/// <summary>Abre (o trae al frente) la ventana de partidas.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class PartidasCommand : IExternalCommand
{
	private static PartidasForm _form;

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

		var handler = new PartidasHandler();
		var externalEvent = ExternalEvent.Create(handler);
		_form = new PartidasForm(handler, externalEvent);
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
public class PartidasAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
