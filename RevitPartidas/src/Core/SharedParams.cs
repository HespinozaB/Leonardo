using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>
/// Los 6 parámetros DYCUSA_PARTIDA (texto, de ejemplar, en Datos de identidad). Son compartidos con GUID fijos, así que son
/// los mismos en todos los modelos y se pueden usar en tablas y etiquetas.
/// </summary>
public static class SharedParams
{
	public const string Prefix = "DYCUSA_PARTIDA ";

	public const string GroupName = "DYCUSA";

	public static readonly string[] Names = Enumerable.Range(1, PartidaCode.MaxLevels).Select(i => Prefix + i).ToArray();

	private static readonly Guid[] Guids =
	{
		new Guid("98e99e07-cb5f-471a-90ae-15524696d506"),
		new Guid("2333121c-f470-4841-a0bc-e7092b9d5dd2"),
		new Guid("aa3aa6e6-a41d-438f-960d-075d8dc4d83b"),
		new Guid("5119db6b-cc1f-4d86-88e6-e83bc911de11"),
		new Guid("9a48f1b1-27e7-4ecf-887f-05eed67ece7e"),
		new Guid("385707d0-2e01-428f-80c8-6332da246398")
	};

	/// <summary>Archivo de parámetros compartidos que se deja al usuario (para familias de etiquetas, por ejemplo).</summary>
	public static string SharedFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "DYCUSA_Parametros.txt");

	/// <summary>Cuántos de los 6 parámetros están ya vinculados en el modelo.</summary>
	public static int CountBound(Document doc) => FindBound(doc).Count(d => d != null);

	/// <summary>Las definiciones vinculadas de DYCUSA_PARTIDA 1…6 (null si alguna no existe).</summary>
	public static Definition[] FindBound(Document doc)
	{
		var found = new Definition[Names.Length];
		DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
		while (iterator.MoveNext())
		{
			int index = Array.IndexOf(Names, iterator.Key?.Name);
			if (index >= 0)
			{
				found[index] = iterator.Key;
			}
		}

		return found;
	}

	/// <summary>
	/// Crea (o amplía a todas las categorías de modelo) los 6 parámetros. Debe llamarse dentro de una transacción.
	/// </summary>
	public static string Ensure(Document doc)
	{
		Application app = doc.Application;
		CategorySet categories = ModelCategories(doc);
		Definition[] bound = FindBound(doc);

		string previousFile = app.SharedParametersFilename;
		int created = 0;
		int extended = 0;
		try
		{
			DefinitionFile file = OpenSharedFile(app);
			DefinitionGroup group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);
			for (int i = 0; i < Names.Length; i++)
			{
				var binding = new InstanceBinding(categories);
				if (bound[i] != null)
				{
					// Ya existe: se vuelve a vincular con todas las categorías (las anteriores se conservan).
					if (doc.ParameterBindings.get_Item(bound[i]) is ElementBinding existing)
					{
						foreach (Category category in existing.Categories)
						{
							binding.Categories.Insert(category);
						}
					}

					if (doc.ParameterBindings.ReInsert(bound[i], binding, GroupTypeId.IdentityData))
					{
						extended++;
					}

					continue;
				}

				Definition definition = group.Definitions.get_Item(Names[i]);
				if (definition == null)
				{
					var options = new ExternalDefinitionCreationOptions(Names[i], SpecTypeId.String.Text)
					{
						GUID = Guids[i],
						Description = $"Código de la partida del presupuesto, nivel {i + 1}.",
						UserModifiable = true,
						Visible = true
					};
					definition = group.Definitions.Create(options);
				}

				if (doc.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData))
				{
					created++;
				}
			}
		}
		finally
		{
			AllowVaryBetweenGroups(doc);

			// No se cambia el archivo de parámetros compartidos que el usuario tenía configurado.
			try
			{
				app.SharedParametersFilename = previousFile ?? string.Empty;
			}
			catch (Exception)
			{
			}
		}

		return $"Parámetros DYCUSA: {created} creado(s), {extended} actualizado(s) en {categories.Size} categorías.";
	}

	/// <summary>Permite que cada copia de un grupo tenga su propia partida (si no, Revit no deja escribir en elementos agrupados).</summary>
	private static void AllowVaryBetweenGroups(Document doc)
	{
		foreach (Guid guid in Guids)
		{
			try
			{
				InternalDefinition definition = SharedParameterElement.Lookup(doc, guid)?.GetDefinition();
				if (definition != null && !definition.VariesAcrossGroups)
				{
					definition.SetAllowVaryBetweenGroups(doc, true);
				}
			}
			catch (Exception)
			{
				// Algunos tipos de parámetro no lo admiten: se sigue con los demás.
			}
		}
	}

	private static DefinitionFile OpenSharedFile(Application app)
	{
		string path = SharedFilePath;
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		if (!File.Exists(path))
		{
			// Archivo vacío con la cabecera mínima; Revit escribe los grupos y parámetros.
			File.WriteAllText(path, "# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
		}

		app.SharedParametersFilename = path;
		return app.OpenSharedParameterFile() ?? throw new InvalidOperationException("No se pudo abrir el archivo de parámetros compartidos: " + path);
	}

	/// <summary>Todas las categorías de modelo que admiten parámetros de proyecto.</summary>
	public static CategorySet ModelCategories(Document doc)
	{
		CategorySet set = doc.Application.Create.NewCategorySet();
		foreach (Category category in doc.Settings.Categories)
		{
			if (category.CategoryType == CategoryType.Model && category.AllowsBoundParameters && !category.IsTagCategory)
			{
				set.Insert(category);
			}
		}

		return set;
	}

	/// <summary>Los parámetros DYCUSA del elemento (null en los niveles que no tiene).</summary>
	public static Parameter[] Get(Element element)
	{
		var parameters = new Parameter[Names.Length];
		for (int i = 0; i < Names.Length; i++)
		{
			parameters[i] = element.get_Parameter(Guids[i]) ?? element.LookupParameter(Names[i]);
		}

		return parameters;
	}

	public static IReadOnlyList<Guid> AllGuids => Guids;
}
