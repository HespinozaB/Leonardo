using System.Drawing;
using System.Windows.Forms;

namespace RevitDwgExploder.UI;

/// <summary>Confirmación y opciones de "Explotar Varios DWG's".</summary>
internal sealed class ExplodeOptionsDialog : Form
{
	private readonly CheckBox _adjustScale = new CheckBox();

	private readonly CheckBox _deleteOriginals = new CheckBox();

	public ExplodeOptionsDialog(int count, RevitDwgExploder.Commands.FinderMode mode = RevitDwgExploder.Commands.FinderMode.Dwg)
	{
		bool pdf = mode != RevitDwgExploder.Commands.FinderMode.Dwg;
		bool image = mode == RevitDwgExploder.Commands.FinderMode.Image;
		Text = image ? "EMASY · Explotar Varias Imágenes" : pdf ? "EMASY · Explotar Varios PDF's" : "EMASY · Explotar Varios DWG's";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(470, 230);
		Font = new Font("Segoe UI", 9f);

		var title = new Label
		{
			Text = image ? $"Explotar {count} imagen(es)" : $"Explotar {count} {(pdf ? "PDF" : "DWG")}",
			Font = new Font("Segoe UI", 11f, FontStyle.Bold),
			Location = new Point(16, 14),
			AutoSize = true
		};
		var info = new Label
		{
			Text = image
				? "Cada imagen se vectoriza en su vista, en la misma posición y tamaño: las manchas de color pasan a " +
					"Filled Regions, los trazos a líneas de detalle y los textos (OCR) a TextNotes."
				: pdf
				? "Cada PDF se explota en su vista, en la misma posición y tamaño que la imagen: sus trazos, rellenos y " +
					"textos se convierten en líneas de detalle, Filled Regions y TextNotes."
				: "Cada DWG se explota en su vista (la propia si está \"solo en su vista\"; si es de modelo, la vista " +
					"activa si lo muestra o una planta de su nivel). Se crea una transacción por vista.",
			Location = new Point(18, 44),
			Size = new Size(435, 48)
		};

		_adjustScale.Text = "Ajustar la escala de la vista si los textos del DWG son demasiado pequeños";
		_adjustScale.Checked = true;
		_adjustScale.Location = new Point(18, 100);
		_adjustScale.AutoSize = true;
		_adjustScale.Visible = !pdf;

		_deleteOriginals.Text = image ? "Eliminar las imágenes originales después de explotarlas" : pdf ? "Eliminar los PDF originales después de explotarlos" : "Eliminar los DWG originales después de explotarlos";
		_deleteOriginals.Location = new Point(18, pdf ? 100 : 128);
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
