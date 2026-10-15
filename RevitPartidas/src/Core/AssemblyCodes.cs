using System;
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
	/// árbol: cada código va después de su padre. UTF-16 para conservar tildes y Ñ.
	/// </summary>
	public static string WriteFile(Budget budget, int maxLevels)
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
			text.Append(item.Code).Append('\t').Append(description).Append('\t')
				.Append(item.Depth.ToString(CultureInfo.InvariantCulture)).Append('\t').Append("\r\n");
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
