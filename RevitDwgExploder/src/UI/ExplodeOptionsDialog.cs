using System.Drawing;
using System.Windows.Forms;

namespace RevitDwgExploder.UI;

/// <summary>Confirmación y opciones de "Explotar Varios DWG's".</summary>
internal sealed class ExplodeOptionsDialog : Form
{
	private readonly CheckBox _adjustScale = new CheckBox();

	private readonly CheckBox _deleteOriginals = new CheckBox();

	public ExplodeOptionsDialog(int count)
	{
		Text = "EMASY · Explotar Varios DWG's";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(470, 230);
		Font = new Font("Segoe UI", 9f);

		var title = new Label
		{
			Text = $"Explotar {count} DWG",
			Font = new Font("Segoe UI", 11f, FontStyle.Bold),
			Location = new Point(16, 14),
			AutoSize = true
		};
		var info = new Label
		{
			Text = "Cada DWG se explota en su vista (la propia si está \"solo en su vista\"; si es de modelo, la vista " +
				"activa si lo muestra o una planta de su nivel). Se crea una transacción por vista.",
			Location = new Point(18, 44),
			Size = new Size(435, 48)
		};

		_adjustScale.Text = "Ajustar la escala de la vista si los textos del DWG son demasiado pequeños";
		_adjustScale.Checked = true;
		_adjustScale.Location = new Point(18, 100);
		_adjustScale.AutoSize = true;

		_deleteOriginals.Text = "Eliminar los DWG originales después de explotarlos";
		_deleteOriginals.Location = new Point(18, 128);
		_deleteOriginals.AutoSize = true;

		var ok = new Button { Text = "Explotar", DialogResult = DialogResult.OK, Location = new Point(270, 186), Size = new Size(90, 30) };
		ok.Font = new Font(ok.Font, FontStyle.Bold);
		var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(366, 186), Size = new Size(90, 30) };

		Controls.AddRange(new Control[] { title, info, _adjustScale, _deleteOriginals, ok, cancel });
		AcceptButton = ok;
		CancelButton = cancel;
	}

	public bool AdjustScale => _adjustScale.Checked;

	public bool DeleteOriginals => _deleteOriginals.Checked;
}
