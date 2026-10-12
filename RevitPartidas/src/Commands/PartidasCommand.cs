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
	CreateParameters,
	Write,
	Clear,
	Select,
	ReadSelection
}

/// <summary>Ejecuta las acciones de la ventana en el contexto válido de la API de Revit (ExternalEvent).</summary>
internal sealed class PartidasHandler : IExternalEventHandler
{
	public PartidasAction Action;

	public Budget Budget;

	public IList<string> Keywords = new List<string>();

	public bool PreferSource;

	/// <summary>Para Write: elemento → código más profundo a escribir (los 6 niveles se derivan de él).</summary>
	public Dictionary<long, string> Assignments = new Dictionary<long, string>();

	/// <summary>Para Clear y Select.</summary>
	public List<long> Ids = new List<long>();

	/// <summary>Modelo al que pertenece la lista mostrada (las escrituras solo se aplican a ese modelo).</summary>
	public string DocumentTitle;

	public Action<ScanResult, string, string> OnResult;

	public Action<List<long>> OnSelection;

	public void Execute(UIApplication app)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		Document doc = uiDoc?.Document;
		if (doc == null)
		{
			OnResult?.Invoke(new ScanResult(), string.Empty, "No hay ningún modelo abierto.");
			return;
		}

		try
		{
			switch (Action)
			{
				case PartidasAction.Select:
					Select(uiDoc);
					return;
				case PartidasAction.ReadSelection:
					OnSelection?.Invoke(uiDoc.Selection.GetElementIds().Select(id => id.Value).ToList());
					return;
			}

			string message = null;
			bool sameModel = string.IsNullOrEmpty(DocumentTitle) || DocumentTitle == doc.Title;
			if (Action != PartidasAction.Scan && Action != PartidasAction.CreateParameters && !sameModel)
			{
				message = "El modelo activo cambió: la lista se actualizó, vuelve a seleccionar los elementos.";
			}
			else if (Action == PartidasAction.CreateParameters)
			{
				message = Run(doc, "EMASY: crear parámetros DYCUSA", () => SharedParams.Ensure(doc));
			}
			else if (Action == PartidasAction.Write)
			{
				message = Run(doc, "EMASY: escribir partidas DYCUSA", () => Write(doc, Assignments));
			}
			else if (Action == PartidasAction.Clear)
			{
				message = Run(doc, "EMASY: limpiar partidas DYCUSA", () => Write(doc, Ids.ToDictionary(id => id, id => (string)null)));
			}

			ScanResult result = ElementScanner.Run(doc, Budget, Keywords, PreferSource);
			if (result.DycusaBound < SharedParams.Names.Length)
			{
				result.Notes.Add("Faltan los parámetros DYCUSA en el modelo: pulsa \"Crear parámetros DYCUSA\".");
			}

			if (result.Notes.Count > 0)
			{
				message = (message == null ? string.Empty : message + "  ·  ") + string.Join("  ·  ", result.Notes);
			}

			OnResult?.Invoke(result, doc.Title, message);
		}
		catch (Exception ex)
		{
			OnResult?.Invoke(null, doc.Title, "Error: " + ex.Message);
		}
	}

	public string GetName() => "EMASY · Partidas DYCUSA";

	private static string Run(Document doc, string name, Func<string> body)
	{
		using var transaction = new Transaction(doc, name);
		transaction.Start();
		string message = body();
		return transaction.Commit() == TransactionStatus.Committed ? message : "Revit rechazó la transacción: no se guardaron cambios.";
	}

	/// <summary>Escribe los 6 niveles a partir del código más profundo (null limpia los 6).</summary>
	private static string Write(Document doc, Dictionary<long, string> assignments)
	{
		int written = 0;
		int missing = 0;
		int failed = 0;
		foreach (KeyValuePair<long, string> assignment in assignments)
		{
			Element element = doc.GetElement(new ElementId(assignment.Key));
			if (element == null)
			{
				failed++;
				continue;
			}

			Parameter[] parameters = SharedParams.Get(element);
			if (parameters.Any(p => p == null))
			{
				missing++;
				continue;
			}

			string[] levels = PartidaCode.Levels(assignment.Value);
			bool ok = true;
			for (int i = 0; i < parameters.Length; i++)
			{
				if (parameters[i].IsReadOnly)
				{
					ok = false;
					break;
				}

				try
				{
					if ((parameters[i].AsString() ?? string.Empty) != levels[i])
					{
						ok &= parameters[i].Set(levels[i]);
					}
				}
				catch (Exception)
				{
					ok = false;
				}
			}

			if (ok)
			{
				written++;
			}
			else
			{
				failed++;
			}
		}

		string text = $"{written} elemento(s) actualizado(s).";
		if (missing > 0)
		{
			text += $" {missing} sin parámetros DYCUSA (crea los parámetros primero).";
		}

		if (failed > 0)
		{
			text += $" {failed} no se pudieron escribir (de solo lectura, en un grupo o vínculo).";
		}

		return text;
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
	}
}

/// <summary>Abre (o trae al frente) la ventana de partidas DYCUSA.</summary>
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
