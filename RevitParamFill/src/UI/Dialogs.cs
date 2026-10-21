using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RevitParamFill.Core;

namespace RevitParamFill.UI;

/// <summary>Editor de pares clave → valor (equivalencias o palabras clave).</summary>
internal sealed class PairsForm : Form
{
	private readonly DataGridView _grid = new DataGridView();

	public PairsForm(string title, string keyHeader, string valueHeader, IDictionary<string, string> pairs, string hint)
	{
		Text = title;
		Font = new Font("Segoe UI", 9f);
		StartPosition = FormStartPosition.CenterParent;
		Size = new Size(620, 480);
		ShowInTaskbar = false;
		MinimizeBox = false;

		var label = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 8, 8, 0), Text = hint, ForeColor = Color.DimGray };
		_grid.Dock = DockStyle.Fill;
		_grid.AllowUserToAddRows = true;
		_grid.AllowUserToDeleteRows = true;
		_grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = keyHeader, Width = 260 });
		_grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = valueHeader, Width = 280 });
		foreach (KeyValuePair<string, string> pair in pairs)
		{
			_grid.Rows.Add(pair.Key, pair.Value);
		}

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
		var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
		var ok = new Button { Text = "Aceptar", AutoSize = true, DialogResult = DialogResult.OK };
		ok.Click += (s, e) => _grid.EndEdit();
		bottom.Controls.AddRange(new Control[] { cancel, ok });
		AcceptButton = ok;
		CancelButton = cancel;
		Controls.Add(_grid);
		Controls.Add(label);
		Controls.Add(bottom);
	}

	public List<KeyValuePair<string, string>> Pairs =>
		_grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
			.Select(r => new KeyValuePair<string, string>((r.Cells[0].Value as string ?? string.Empty).Trim(), (r.Cells[1].Value as string ?? string.Empty).Trim()))
			.Where(p => p.Key.Length > 0).GroupBy(p => p.Key).Select(g => g.Last()).ToList();
}

/// <summary>Cómo deducir el valor de una regla cuando no hay dato.</summary>
internal sealed class InferForm : Form
{
	public InferForm(FillRule rule)
	{
		Text = $"Si falta el dato · {rule.Target}";
		Font = new Font("Segoe UI", 9f);
		StartPosition = FormStartPosition.CenterParent;
		Size = new Size(560, 380);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;

		var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
		layout.Controls.Add(new Label
		{
			AutoSize = true,
			MaximumSize = new Size(510, 0),
			Text = "Los valores deducidos no se escriben solos: aparecen como \"Inferido\" en la vista previa con su motivo, y tú los aceptas." +
				(rule.Source == SourceKind.Sector ? " Para sectores, primero se usa el sector más cercano." : string.Empty) +
				(rule.Source == SourceKind.Nivel ? " Para el nivel, primero se usa el nivel por la altura del elemento." : string.Empty)
		});
		var sameType = new CheckBox { AutoSize = true, Text = "El valor más frecuente entre los elementos de su mismo tipo", Checked = rule.InferSameType };
		var nearest = new CheckBox { AutoSize = true, Text = "El valor del elemento más cercano que sí lo tiene (hasta 15 m, primero en su nivel)", Checked = rule.InferNearest };
		var keywords = new CheckBox { AutoSize = true, Text = "Palabras clave en familia/tipo/categoría y parecido con valores ya usados", Checked = rule.InferKeywords };
		var keywordsButton = new Button { AutoSize = true, Text = $"Palabras clave… ({rule.Keywords.Count})", Margin = new Padding(20, 0, 0, 8) };
		var working = new Dictionary<string, string>(rule.Keywords, StringComparer.Ordinal);
		keywordsButton.Click += (s, e) =>
		{
			using var dialog = new PairsForm($"Palabras clave · {rule.Target}", "Palabra en el nombre", "Valor", working,
				"Si el nombre de la familia, el tipo o la categoría contiene la palabra, se propone el valor (p. ej. drywall → PLACA DE YESO).");
			if (dialog.ShowDialog(this) == DialogResult.OK)
			{
				working = dialog.Pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
				keywordsButton.Text = $"Palabras clave… ({working.Count})";
			}
		};
		var defaultLabel = new Label { AutoSize = true, Text = "Si nada de lo anterior da resultado, valor por defecto (vacío = no proponer):", Margin = new Padding(0, 8, 0, 2) };
		var defaultValue = new TextBox { Width = 300, Text = rule.Default };
		layout.Controls.AddRange(new Control[] { sameType, nearest, keywords, keywordsButton, defaultLabel, defaultValue });

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
		var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
		var ok = new Button { Text = "Aceptar", AutoSize = true, DialogResult = DialogResult.OK };
		ok.Click += (s, e) =>
		{
			rule.InferSameType = sameType.Checked;
			rule.InferNearest = nearest.Checked;
			rule.InferKeywords = keywords.Checked;
			rule.Keywords = working;
			rule.Default = defaultValue.Text.Trim();
		};
		bottom.Controls.AddRange(new Control[] { cancel, ok });
		AcceptButton = ok;
		CancelButton = cancel;
		Controls.Add(layout);
		Controls.Add(bottom);
	}
}
