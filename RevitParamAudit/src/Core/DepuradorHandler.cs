using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamAudit.UI;

namespace RevitParamAudit.Core;

internal enum DepAction
{
	Refresh,
	Delete,
	Locate,
	Select,
	OpenNext
}

/// <summary>Ejecuta las acciones de la ventana en el contexto válido de la API de Revit (ExternalEvent).</summary>
internal sealed class DepuradorHandler : IExternalEventHandler
{
	private readonly IDepurador _tool;

	public DepuradorHandler(IDepurador tool)
	{
		_tool = tool;
	}

	public DepAction Action;

	/// <summary>Análisis profundo (solo parámetros).</summary>
	public bool Deep;

	public List<long> Ids = new List<long>();

	/// <summary>Modelo al que pertenece la lista mostrada (las acciones solo se aplican a ese modelo).</summary>
	public string DocumentTitle;

	/// <summary>Filas nuevas, título del modelo y mensaje.</summary>
	public Action<List<DepRow>, string, string> OnRows;

	/// <summary>Solo un mensaje (la lista no cambió).</summary>
	public Action<string> OnMessage;

	public void Execute(UIApplication app)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		if (uiDoc == null)
		{
			OnRows?.Invoke(new List<DepRow>(), string.Empty, "No hay ningún modelo abierto.");
			return;
		}

		Document doc = uiDoc.Document;
		try
		{
			if (Action == DepAction.OpenNext)
			{
				ToolKind? next = DepuradorTools.Next(_tool.Kind);
				OnMessage?.Invoke(null);
				if (next != null)
				{
					DepuradorLauncher.Show(app, next.Value);
				}

				return;
			}

			string message = null;
			bool sameModel = string.IsNullOrEmpty(DocumentTitle) || DocumentTitle == doc.Title;
			if (Action != DepAction.Refresh && !sameModel)
			{
				message = "El modelo activo cambió: la lista se actualizó, vuelve a elegir.";
			}
			else
			{
				switch (Action)
				{
					case DepAction.Delete:
						message = _tool.Delete(uiDoc, Ids);
						break;
					case DepAction.Locate:
						OnMessage?.Invoke(_tool.Locate(uiDoc, Ids));
						return;
					case DepAction.Select:
						OnMessage?.Invoke(_tool.Select(uiDoc, Ids));
						return;
				}
			}

			var notes = new List<string>();
			List<DepRow> rows = _tool.Collect(uiDoc, Deep, notes);
			if (notes.Count > 0)
			{
				message = (message == null ? string.Empty : message + "  ·  ") + string.Join("  ·  ", notes);
			}

			OnRows?.Invoke(rows, doc.Title, message);
		}
		catch (Exception ex)
		{
			OnMessage?.Invoke("Error: " + ex.Message);
		}
	}

	public string GetName() => "EMASY · " + _tool.Title;
}

/// <summary>Abre (o trae al frente) la ventana de un depurador.</summary>
internal static class DepuradorLauncher
{
	private static readonly Dictionary<ToolKind, DepuradorForm> Forms = new Dictionary<ToolKind, DepuradorForm>();

	public static Result Show(UIApplication app, ToolKind kind)
	{
		UIDocument uiDoc = app.ActiveUIDocument;
		if (uiDoc == null)
		{
			return Result.Cancelled;
		}

		if (Forms.TryGetValue(kind, out DepuradorForm existing) && !existing.IsDisposed)
		{
			if (existing.WindowState == System.Windows.Forms.FormWindowState.Minimized)
			{
				existing.WindowState = System.Windows.Forms.FormWindowState.Normal;
			}

			existing.Activate();
			existing.RequestRefresh();
			return Result.Succeeded;
		}

		IDepurador tool = DepuradorTools.Create(kind);
		var notes = new List<string>();
		List<DepRow> rows = tool.Collect(uiDoc, false, notes);
		var handler = new DepuradorHandler(tool);
		var externalEvent = ExternalEvent.Create(handler);
		var form = new DepuradorForm(tool, handler, externalEvent);
		form.SetRows(rows, uiDoc.Document.Title, notes.Count > 0 ? string.Join("  ·  ", notes) : null);
		form.Show(new WindowHandle(app.MainWindowHandle));
		Forms[kind] = form;
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
