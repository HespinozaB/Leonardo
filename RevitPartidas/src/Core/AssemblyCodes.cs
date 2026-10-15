using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>
/// Código de montaje (Assembly Code) y Descripción de montaje de Revit. Son parámetros de tipo. El paso 1 genera, a partir del
/// presupuesto, el archivo de códigos de montaje y lo carga en el modelo, para que cada tipo pueda llevar su partida.
/// </summary>
public static class AssemblyCodes
{
	public static string FilePath(Budget budget) => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY",
		"Codigos de montaje - " + Path.GetFileNameWithoutExtension(budget.SourcePath) + ".txt");

	/// <summary>
	/// Escribe el archivo con el formato de Revit (código, descripción, nivel, categoría; separados por tabulación), en el orden del
	/// árbol: cada código va después de su padre. UTF-16 para conservar tildes y Ñ. Revit cuelga cada código del último código
	/// anterior de un nivel menos; los más profundos que <paramref name="maxRevitLevel"/> se escriben con ese nivel, así quedan
	/// junto a su padre, dentro de la misma rama, en vez de sueltos en la raíz.
	/// </summary>
	public static string WriteFile(Budget budget, int maxLevels, int maxRevitLevel = int.MaxValue)
	{
		string path = FilePath(budget);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		var text = new StringBuilder();
		void Write(BudgetItem item)
		{
			if (item.Depth > maxLevels)
			{
				return;
			}

			string description = (item.Description ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
			int level = Math.Min(item.Depth, maxRevitLevel);
			text.Append(item.Code).Append('\t').Append(description).Append('\t')
				.Append(level.ToString(CultureInfo.InvariantCulture)).Append('\t').Append("\r\n");
			foreach (BudgetItem child in item.Children.OrderBy(c => c.Code, StringComparer.Ordinal))
			{
				Write(child);
			}
		}

		foreach (BudgetItem root in budget.Roots.OrderBy(r => r.Code, StringComparer.Ordinal))
		{
			Write(root);
		}

		File.WriteAllText(path, text.ToString(), Encoding.Unicode);
		return path;
	}

	/// <summary>
	/// Genera y carga la tabla en el modelo (dentro de una transacción) y comprueba que cada código quedó bajo su padre. Si Revit
	/// dejó códigos fuera de su rama (admite menos niveles que el presupuesto), vuelve a escribir los más profundos al nivel
	/// máximo que sí respetó y los carga de nuevo.
	/// </summary>
	public static string Generate(Document doc, Budget budget, int maxLevels)
	{
		string path = WriteFile(budget, maxLevels);
		string message = Load(doc, path);
		List<(string Key, string Parent, int Depth)> wrong = Misplaced(doc, budget);
		if (wrong.Count == 0)
		{
			return message;
		}

		int maxOk = Math.Max(1, wrong.Min(w => w.Depth) - 1);
		path = WriteFile(budget, maxLevels, maxOk);
		message = Load(doc, path);
		List<(string Key, string Parent, int Depth)> still = Misplaced(doc, budget, flattenedFrom: maxOk);
		int flattened = budget.All.Count(i => i.Depth > maxOk && i.Depth <= maxLevels);
		message += $" Revit admite {maxOk} niveles en el código de montaje: los {flattened} códigos más profundos quedan junto a su partida " +
			$"de nivel {maxOk}, dentro de la misma rama.";
		if (still.Count > 0)
		{
			message += $" Atención: {still.Count} código(s) siguen fuera de su rama (p. ej. {still[0].Key} cuelga de \"{still[0].Parent}\").";
		}

		return message;
	}

	/// <summary>
	/// Códigos cuyo padre en la tabla de Revit no es el del presupuesto. Con <paramref name="flattenedFrom"/>, los más profundos
	/// se dan por buenos si cuelgan del padre de su ancestro de ese nivel.
	/// </summary>
	private static List<(string Key, string Parent, int Depth)> Misplaced(Document doc, Budget budget, int flattenedFrom = int.MaxValue)
	{
		var wrong = new List<(string, string, int)>();
		foreach (KeyBasedTreeEntry entry in AssemblyCodeTable.GetAssemblyCodeTable(doc).GetKeyBasedTreeEntries())
		{
			BudgetItem item = budget.Find(entry.Key);
			if (item == null)
			{
				continue;
			}

			BudgetItem expected = item.Parent;
			while (expected != null && expected.Depth >= flattenedFrom)
			{
				expected = expected.Parent;
			}

			string parent = entry.ParentKey ?? string.Empty;
			if (parent != (expected?.Code ?? string.Empty))
			{
				wrong.Add((entry.Key, parent, item.Depth));
			}
		}

		return wrong;
	}

	/// <summary>Carga el archivo como tabla de códigos de montaje del modelo. Debe llamarse dentro de una transacción.</summary>
	public static string Load(Document doc, string path)
	{
		AssemblyCodeTable table = AssemblyCodeTable.GetAssemblyCodeTable(doc);
		ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
		ExternalResourceReference reference = ExternalResourceReference.CreateLocalResource(doc,
			ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable, modelPath, PathType.Absolute);
		var results = new KeyBasedTreeEntriesLoadResults();
		table.LoadFrom(reference, results);
		var failures = results.GetFailureMessages();
		int entries = table.GetKeyBasedTreeEntries().Count();
		string message = $"Tabla de códigos de montaje cargada: {entries} códigos ({Path.GetFileName(path)}).";
		if (failures.Count > 0)
		{
			message += " Avisos: " + string.Join(" | ", failures.Take(3));
		}

		return message;
	}

	public static Parameter CodeParameter(Element type) => type?.get_Parameter(BuiltInParameter.UNIFORMAT_CODE);

	public static string Code(Element type) => CodeParameter(type)?.AsString()?.Trim() ?? string.Empty;

	public static string Description(Element type) => type?.get_Parameter(BuiltInParameter.UNIFORMAT_DESCRIPTION)?.AsString()?.Trim() ?? string.Empty;
}
