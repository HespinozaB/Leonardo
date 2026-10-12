using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using RevitPartidas.Commands;
using RevitPartidas.Core;

namespace RevitPartidas.UI;

/// <summary>Acciones del paso 4: escribir, asignar, vaciar, resolver en 3D y exportar.</summary>
internal sealed partial class PartidasForm
{
	private Resolve3DForm _resolveForm;

	/// <summary>Elementos en conflicto y opciones de la revisión 3D en curso.</summary>
	private List<ElementRow> _resolveRows = new List<ElementRow>();
	private List<Review3DOption> _resolveOptions = new List<Review3DOption>();

	private bool ParametersReady()
	{
		if (_rows.Count == 0 || _rows.Any(r => r.HasDycusaParameters))
		{
			return true;
		}

		if (MessageBox.Show(this, "El modelo aún no tiene los parámetros " + string.Join(", ", _config.Names) + ".\n¿Crearlos ahora?\n\n" +
				"Después vuelve a pulsar el botón.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			Raise(PartidasAction.CreateParameters, "Creando parámetros…");
		}

		return false;
	}

	private void Write(Dictionary<long, string[]> assignments, string busyText, bool reset3D = false)
	{
		_handler.Assignments = assignments;
		_handler.Reset3DAfterWrite = reset3D;
		Raise(PartidasAction.Write, busyText);
	}

	private void WritePending()
	{
		List<ElementRow> marked = AllNodes(_results.Nodes).Any(n => n.Checked) ? Targets() : _rows;
		List<ElementRow> targets = marked.Where(r => r.Status == RowStatus.Pendiente || r.Status == RowStatus.Parcial).ToList();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "No hay elementos pendientes de escribir" + (marked == _rows ? "." : " entre los marcados."), Text,
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!ParametersReady())
		{
			return;
		}

		int partial = targets.Count(r => r.Status == RowStatus.Parcial);
		string question = $"Escribir {string.Join(", ", _config.Names.Take(2))}… en {targets.Count} elemento(s)" +
			(marked == _rows ? " (todos los pendientes)?" : " marcados?") +
			(partial > 0 ? $"\n\n{partial} de ellos solo tienen los niveles superiores (por los enlaces)." : string.Empty) +
			"\n\nLos que están por revisar no se tocan. Se deshace con Ctrl+Z en Revit.";
		if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			Write(targets.ToDictionary(r => r.Id, r => r.Proposed), "Escribiendo partidas…");
		}
	}

	private void AssignNode()
	{
		if (!(_budgetTree.SelectedNode?.Tag is BudgetItem item))
		{
			MessageBox.Show(this, "Marca una partida en el presupuesto (izquierda).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		List<ElementRow> targets = Targets();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "Marca los grupos o elementos en la lista de la derecha (o pulsa \"Usar selección de Revit\").", Text,
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!ParametersReady())
		{
			return;
		}

		string question = $"Asignar la partida\n\n{item.Code}  {item.Description}\n\na {targets.Count} elemento(s)?\nSe escriben los {_config.Levels} niveles.";
		if (!item.IsLeaf)
		{
			question += "\n\nAtención: es un título, no una partida final del presupuesto.";
		}

		int mismatch = targets.Count(r => r.Linked != null && r.Linked.Any(l => l.Length > 0 && !PartidaCode.IsAncestorOrSelf(l, item.Code)));
		if (mismatch > 0)
		{
			question += $"\n\nAtención: en {mismatch} elemento(s) no coincide con el nivel enlazado (p. ej. su ETAPA).";
		}

		int overwritten = targets.Count(r => r.DycusaCode != null && r.DycusaCode != item.Code);
		if (overwritten > 0)
		{
			question += $"\n\n{overwritten} elemento(s) ya tienen otra partida escrita: se reemplaza.";
		}

		if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			string[] levels = PartidaCode.Levels(item.Code, _config.Levels);
			Write(targets.ToDictionary(r => r.Id, r => levels), "Asignando partida…");
		}
	}

	private void ClearTargets()
	{
		List<ElementRow> targets = Targets();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "Marca los grupos o elementos a vaciar.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, $"Vaciar los {_config.Levels} parámetros de nivel en {targets.Count} elemento(s)?\n(Se deshace con Ctrl+Z en Revit.)",
				Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
		{
			string[] empty = PartidaCode.Levels(null, _config.Levels);
			Write(targets.ToDictionary(r => r.Id, r => empty), "Vaciando…");
		}
	}

	private void SelectInRevit(IEnumerable<long> ids)
	{
		_handler.Ids = ids.ToList();
		if (_handler.Ids.Count == 0)
		{
			_status.Text = "No hay elementos para seleccionar.";
			return;
		}

		Raise(PartidasAction.Select, null);
	}

	#region Resolver en 3D

	private void Resolve3D()
	{
		List<ElementRow> targets = Targets().Where(r => r.Candidates.Count > 0).ToList();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "Marca o selecciona un grupo de \"Conflicto\" (o \"No coincide\"): son los que tienen varias opciones.", Text,
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		// Las opciones más frecuentes primero; como máximo tantas como colores.
		List<string> candidates = targets.SelectMany(r => r.Candidates).GroupBy(c => c).OrderByDescending(g => g.Count())
			.Select(g => g.Key).Take(Review3D.OptionColors.Length).ToList();
		var conflictIds = new HashSet<long>(targets.Select(r => r.Id));
		var levels = new HashSet<string>(targets.Select(r => r.Level));
		_resolveRows = targets;
		_resolveOptions = new List<Review3DOption>();
		for (int i = 0; i < candidates.Count; i++)
		{
			// Referencias: elementos que ya tienen esa partida sin dudas, primero los de los mismos niveles.
			List<long> references = _rows
				.Where(r => r.ProposedCode == candidates[i] && (r.Status == RowStatus.Ok || r.Status == RowStatus.Pendiente) && !conflictIds.Contains(r.Id))
				.OrderBy(r => levels.Contains(r.Level) ? 0 : 1)
				.Take(1500)
				.Select(r => r.Id)
				.ToList();
			_resolveOptions.Add(new Review3DOption
			{
				Code = candidates[i],
				Description = _budget?.Find(candidates[i])?.Description ?? "(no está en el presupuesto)",
				ReferenceIds = references,
				Color = Review3D.OptionColors[i]
			});
		}

		_handler.Ids = targets.Select(r => r.Id).ToList();
		_handler.Options = _resolveOptions;
		Raise(PartidasAction.Show3D, "Preparando la vista 3D…");

		_resolveForm?.Close();
		_resolveForm = new Resolve3DForm(targets.Count, _resolveOptions,
			choose: code => Safe(() => ChooseResolution(code)),
			pick: () => Safe(() => Raise(PartidasAction.Pick3D, "Haz clic en Revit en un elemento del color correcto (Esc para cancelar)…")),
			fromTree: () => Safe(() =>
			{
				if (_budgetTree.SelectedNode?.Tag is BudgetItem item)
				{
					ChooseResolution(item.Code);
				}
				else
				{
					MessageBox.Show(_resolveForm, "Marca primero la partida en el presupuesto (izquierda de la ventana principal).", Text);
				}
			}),
			close: () => Safe(() =>
			{
				if (!_busy)
				{
					Raise(PartidasAction.Reset3D, null);
				}
			}));
		_resolveForm.Show(this);
	}

	private void SetPicked(long? id)
	{
		Done(null);
		if (id == null)
		{
			_status.Text = "Clic cancelado.";
			return;
		}

		Review3DOption option = _resolveOptions.FirstOrDefault(o => o.ReferenceIds.Contains(id.Value));
		if (option != null)
		{
			ChooseResolution(option.Code);
		}
		else if (_resolveRows.Any(r => r.Id == id.Value))
		{
			MessageBox.Show(this, "Ese elemento es uno de los que están en conflicto (rojo). Haz clic en un elemento de color azul, verde, naranja o morado, o usa los botones.",
				Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		else
		{
			_status.Text = $"El elemento {id} no es de ninguna de las opciones mostradas.";
		}
	}

	private void ChooseResolution(string code)
	{
		BudgetItem item = _budget?.Find(code);
		if (MessageBox.Show(_resolveForm ?? (IWin32Window)this,
				$"Asignar\n\n{code}  {item?.Description}\n\na los {_resolveRows.Count} elemento(s) en conflicto?", Text,
				MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}

		if (!ParametersReady())
		{
			return;
		}

		string[] levels = PartidaCode.Levels(code, _config.Levels);
		Write(_resolveRows.ToDictionary(r => r.Id, r => levels), "Asignando y quitando colores…", reset3D: true);
	}

	#endregion

	#region CSV

	private void ExportCsv()
	{
		using var dialog = new SaveFileDialog
		{
			Title = "Exportar cobertura y elementos",
			Filter = "CSV (*.csv)|*.csv",
			FileName = $"Partidas - {_documentTitle}.csv"
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		string basePath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? string.Empty, Path.GetFileNameWithoutExtension(dialog.FileName));
		string coveragePath = basePath + " - cobertura.csv";
		string elementsPath = basePath + " - elementos.csv";
		if (_budget != null)
		{
			WriteCsv(coveragePath, CoverageLines());
		}

		WriteCsv(elementsPath, ElementLines());
		_status.Text = _budget != null
			? $"Exportado: {Path.GetFileName(coveragePath)} y {Path.GetFileName(elementsPath)}"
			: $"Exportado: {Path.GetFileName(elementsPath)} (carga el presupuesto para exportar también la cobertura)";
	}

	/// <summary>Por partida: metrado del presupuesto frente a la cantidad BIM de sus elementos (según su unidad).</summary>
	private IEnumerable<string[]> CoverageLines()
	{
		yield return new[] { "Código", "Descripción", "Nivel", "Und.", "Metrado ppto.", "Elementos BIM", "Cantidad BIM", "Diferencia", "Estado", "Observación ppto." };
		ILookup<string, ElementRow> byCode = _rows.Where(r => r.ProposedCode != null).ToLookup(r => r.ProposedCode, StringComparer.Ordinal);
		foreach (BudgetItem item in _budget.All.OrderBy(i => i.Code, StringComparer.Ordinal))
		{
			int count = Count(item.Code);
			string quantity = string.Empty;
			string difference = string.Empty;
			string state = string.Empty;
			if (item.IsLeaf)
			{
				double? bim = Quantity(item.Unit, byCode[item.Code].ToList());
				quantity = bim.HasValue ? Number(bim.Value) : string.Empty;
				if (bim.HasValue && item.Metrado.HasValue)
				{
					difference = Number(bim.Value - item.Metrado.Value);
				}

				state = count > 0 ? "Con elementos" : item.WithoutBimTable ? "Sin tabla en el modelo BIM" : "Sin elementos";
			}

			yield return new[]
			{
				item.Code, item.Description, item.Depth.ToString(CultureInfo.InvariantCulture), item.Unit,
				item.Metrado.HasValue ? Number(item.Metrado.Value) : string.Empty, count.ToString(CultureInfo.InvariantCulture),
				quantity, difference, state, item.Observation ?? string.Empty
			};
		}
	}

	private IEnumerable<string[]> ElementLines()
	{
		var header = new List<string> { "Id", "Estado", "Categoría", "Familia : Tipo", "Nivel", "Grupo / nota", "Parámetro de origen", "Código de origen", "Opciones" };
		header.AddRange(_config.Names.Select(n => n + " (escrito)"));
		header.AddRange(_config.Names.Select(n => n + " (propuesto)"));
		header.AddRange(new[] { "Nota", "Área (m²)", "Longitud (m)", "Volumen (m³)" });
		yield return header.ToArray();
		foreach (ElementRow row in _rows.Where(r => r.InCodedCategory || (r.Status != RowStatus.SinPartida && r.Status != RowStatus.Parcial))
					 .OrderBy(r => r.Category).ThenBy(r => r.Status).ThenBy(r => r.ProposedCode ?? "~", StringComparer.Ordinal))
		{
			var cells = new List<string>
			{
				row.Id.ToString(CultureInfo.InvariantCulture), StatusText[row.Status], row.Category, row.FamilyType, row.Level, GroupLabel(row),
				row.SourceParam ?? string.Empty, row.SourceCode ?? string.Empty, string.Join(" / ", row.Candidates)
			};
			cells.AddRange(Enumerable.Range(0, _config.Levels).Select(i => i < row.Current.Length ? row.Current[i] ?? string.Empty : string.Empty));
			cells.AddRange(Enumerable.Range(0, _config.Levels).Select(i => i < row.Proposed.Length ? row.Proposed[i] : string.Empty));
			cells.AddRange(new[] { row.Note ?? string.Empty, Number(row.Area), Number(row.Length), Number(row.Volume) });
			yield return cells.ToArray();
		}
	}

	/// <summary>Suma la magnitud que corresponde a la unidad (m² → área, m → longitud, m³ → volumen, und → cantidad).</summary>
	private static double? Quantity(string unit, List<ElementRow> rows)
	{
		string u = BudgetReader.Simplify(unit).Replace("²", "2").Replace("³", "3").Replace(".", string.Empty);
		switch (u)
		{
			case "m2":
				return rows.Sum(r => r.Area);
			case "m3":
				return rows.Sum(r => r.Volume);
			case "m":
			case "ml":
				return rows.Sum(r => r.Length);
			case "und":
			case "u":
			case "un":
			case "pza":
			case "jgo":
			case "glb":
				return rows.Count;
			default:
				return null;
		}
	}

	private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

	private static void WriteCsv(string path, IEnumerable<string[]> lines)
	{
		// Separador ";" y UTF-8 con BOM: abre bien en Excel en español.
		using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
		foreach (string[] line in lines)
		{
			writer.WriteLine(string.Join(";", line.Select(Escape)));
		}
	}

	private static string Escape(string value)
	{
		value ??= string.Empty;
		return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
	}

	#endregion
}
