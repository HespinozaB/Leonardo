using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using RevitPartidas.Commands;

namespace RevitPartidas.UI;

/// <summary>
/// Ventana pequeña (siempre encima) mientras se revisa un conflicto en la vista 3D: la leyenda de colores y un botón por opción,
/// "elegir con un clic en el modelo" y "usar la partida marcada en el presupuesto". Al cerrarla se quitan los colores.
/// </summary>
internal sealed class Resolve3DForm : Form
{
	private readonly Action _close;

	private bool _closing;

	public Resolve3DForm(int conflictCount, IList<Review3DOption> options, Action<string> choose, Action pick, Action fromTree, Action close)
	{
		_close = close;
		Text = "EMASY · Resolver en 3D";
		Font = new Font("Segoe UI", 9f);
		FormBorderStyle = FormBorderStyle.SizableToolWindow;
		StartPosition = FormStartPosition.Manual;
		Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 460, Screen.PrimaryScreen.WorkingArea.Top + 120);
		Size = new Size(440, 160 + options.Count * 62);
		TopMost = true;
		ShowInTaskbar = false;

		var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10), AutoScroll = true };
		layout.Controls.Add(new Label
		{
			AutoSize = true,
			MaximumSize = new Size(400, 0),
			Text = $"En rojo: {conflictCount} elemento(s) con varias partidas posibles. En colores: elementos que ya tienen cada opción. " +
				"¿Cuál es la correcta para los rojos?"
		});

		foreach (Review3DOption option in options)
		{
			var button = new Button
			{
				Width = 400,
				Height = 54,
				TextAlign = ContentAlignment.MiddleLeft,
				BackColor = option.Color,
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat,
				Font = new Font(Font, FontStyle.Bold),
				Text = $"{option.Code}  {Shorten(option.Description, 70)}\n{option.ReferenceIds.Count} elemento(s) de referencia en este color"
			};
			string code = option.Code;
			button.Click += (s, e) => choose(code);
			layout.Controls.Add(button);
		}

		var pickButton = new Button { Width = 400, Height = 30, Text = "Elegir con un clic en el modelo (en un elemento de color)" };
		pickButton.Click += (s, e) => pick();
		layout.Controls.Add(pickButton);
		var treeButton = new Button { Width = 400, Height = 30, Text = "Ninguna: usar la partida marcada en el presupuesto" };
		treeButton.Click += (s, e) => fromTree();
		layout.Controls.Add(treeButton);
		var closeButton = new Button { Width = 400, Height = 30, Text = "Cerrar sin decidir (quitar colores)" };
		closeButton.Click += (s, e) => Close();
		layout.Controls.Add(closeButton);

		Controls.Add(layout);
	}

	/// <summary>Cerrar tras una decisión: los colores los quita la propia escritura.</summary>
	public void CloseAfterDecision()
	{
		_closing = true;
		Close();
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		base.OnFormClosed(e);
		if (!_closing)
		{
			_close();
		}
	}

	private static string Shorten(string text, int length) =>
		string.IsNullOrEmpty(text) || text.Length <= length ? text : text.Substring(0, length - 1).TrimEnd() + "…";
}
