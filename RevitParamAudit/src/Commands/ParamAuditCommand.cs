using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamAudit.UI;

namespace RevitParamAudit.Commands;

internal enum AuditAction
{
	Refresh,
	DeleteUnused
}

/// <summary>Ejecuta las acciones en el contexto válido de la API de Revit (ExternalEvent).</summary>
internal sealed class ParamAuditHandler : IExternalEventHandler
{
	public AuditAction Action;

	/// <summary>Análisis profundo: también revisa los valores de los elementos visibles en las vistas de los planos.</summary>
	public bool Deep;

	public List<long> Ids = new List<long>();

	/// <summary>Modelo al que pertenece la lista mostrada (las acciones solo se aplican a ese modelo).</summary>
	public string DocumentTitle;

	public Action<AuditResult, string, string> OnResult;

	public void Execute(UIApplication app)
	{
		Document doc = app.ActiveUIDocument?.Document;
		if (doc == null)
		{
			OnResult?.Invoke(new AuditResult(), string.Empty, "No hay ningún modelo abierto.");
			return;
		}

		string message = null;
		try
		{
			if (Action == AuditAction.DeleteUnused)
			{
				if (!string.IsNullOrEmpty(DocumentTitle) && DocumentTitle != doc.Title)
				{
					message = "El modelo activo cambió: la lista se actualizó, vuelve a marcar los parámetros.";
				}
				else
				{
					message = DeleteUnused(doc);
				}
			}

			AuditResult result = ParameterAuditor.Run(doc, Deep);
			if (result.Notes.Count > 0)
			{
				message = (message == null ? string.Empty : message + "  ·  ") + string.Join("  ·  ", result.Notes);
			}

			OnResult?.Invoke(result, doc.Title, message);
		}
		catch (Exception ex)
		{
			OnResult?.Invoke(new AuditResult(), doc.Title, "Error: " + ex.Message);
		}
	}

	public string GetName() => "EMASY · Auditoría de parámetros";

	/// <summary>Elimina los parámetros marcados, comprobando de nuevo que siguen sin uso justo antes de borrar.</summary>
	private string DeleteUnused(Document doc)
	{
		HashSet<long> stillUnused = new HashSet<long>(
			ParameterAuditor.Run(doc, Deep).Entries.Where(e => e.IsUnused).Select(e => e.Id));
		int deleted = 0;
		int skipped = 0;
		int failed = 0;
		using (var transaction = new Transaction(doc, "EMASY: eliminar parámetros residuales"))
		{
			transaction.Start();
			foreach (long id in Ids)
			{
				if (!stillUnused.Contains(id))
				{
					skipped++;
					continue;
				}

				var parameter = doc.GetElement(new ElementId(id)) as ParameterElement;
				if (parameter == null || parameter is GlobalParameter)
				{
					failed++;
					continue;
				}

				Definition definition = parameter.GetDefinition();
				try
				{
					doc.Delete(parameter.Id);
					deleted++;
				}
				catch (Exception)
				{
					try
					{
						if (definition != null && doc.ParameterBindings.Remove(definition))
						{
							deleted++;
						}
						else
						{
							failed++;
						}
					}
					catch (Exception)
					{
						failed++;
					}
				}
			}

			if (transaction.Commit() != TransactionStatus.Committed)
			{
				return "No se pudo confirmar la eliminación (Revit rechazó la transacción).";
			}
		}

		string text = $"{deleted} parámetro(s) eliminado(s).";
		if (skipped > 0)
		{
			text += $" {skipped} omitido(s): ya no eran residuales.";
		}

		if (failed > 0)
		{
			text += $" {failed} no se pudieron eliminar.";
		}

		return text;
	}
}

/// <summary>Abre (o trae al frente) la ventana de auditoría de parámetros.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ParamAuditCommand : IExternalCommand
{
	private static ParamAuditForm _form;

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIApplication app = commandData.Application;
		Document doc = app.ActiveUIDocument?.Document;
		if (doc == null)
		{
			return Result.Cancelled;
		}

		if (_form != null && !_form.IsDisposed)
		{
			_form.Activate();
			_form.RequestRefresh();
			return Result.Succeeded;
		}

		AuditResult result;
		try
		{
			result = ParameterAuditor.Run(doc, deep: false);
		}
		catch (Exception ex)
		{
			message = ex.Message;
			return Result.Failed;
		}

		var handler = new ParamAuditHandler();
		var externalEvent = ExternalEvent.Create(handler);
		_form = new ParamAuditForm(handler, externalEvent);
		_form.SetResult(result, doc.Title, null);
		_form.Show(new WindowHandle(app.MainWindowHandle));
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

/// <summary>La auditoría está disponible siempre que haya un modelo abierto.</summary>
public class ParamAuditAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
