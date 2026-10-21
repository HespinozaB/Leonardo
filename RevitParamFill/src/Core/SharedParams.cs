using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitParamFill.Core;

/// <summary>
/// Crea los parámetros destino que falten: texto, de ejemplar, en Datos de identidad, en todas las categorías de modelo. Son
/// compartidos y su GUID depende solo del nombre.
/// </summary>
public static class SharedParams
{
	public const string GroupName = "EMASY Rellenar";

	/// <summary>Archivo de parámetros compartidos que se deja al usuario (para familias de etiquetas, por ejemplo).</summary>
	public static string SharedFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "Rellenar_Parametros.txt");

	public static Guid GuidFor(string name)
	{
		using MD5 md5 = MD5.Create();
		return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("EMASY-RELLENAR:" + name)));
	}

	/// <summary>Las definiciones vinculadas con esos nombres (null en las que no existen).</summary>
	public static Definition[] FindBound(Document doc, IList<string> names)
	{
		var found = new Definition[names.Count];
		DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
		while (iterator.MoveNext())
		{
			int index = names.IndexOf(iterator.Key?.Name);
			if (index >= 0)
			{
				found[index] = iterator.Key;
			}
		}

		return found;
	}

	public static int CountBound(Document doc, IList<string> names) => FindBound(doc, names).Count(d => d != null);

	/// <summary>Crea (o amplía a todas las categorías de modelo) los parámetros. Debe llamarse dentro de una transacción.</summary>
	public static string Ensure(Document doc, IList<string> names)
	{
		Application app = doc.Application;
		CategorySet categories = ModelCategories(doc);
		Definition[] bound = FindBound(doc, names);

		string previousFile = app.SharedParametersFilename;
		int created = 0;
		int extended = 0;
		try
		{
			DefinitionFile file = OpenSharedFile(app);
			DefinitionGroup group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);
			for (int i = 0; i < names.Count; i++)
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

				Definition definition = group.Definitions.get_Item(names[i]);
				if (definition == null)
				{
					var options = new ExternalDefinitionCreationOptions(names[i], SpecTypeId.String.Text)
					{
						GUID = GuidFor(names[i]),
						Description = "Creado por EMASY Rellenar parámetros.",
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
			AllowVaryBetweenGroups(doc, names);

			// No se cambia el archivo de parámetros compartidos que el usuario tenía configurado.
			try
			{
				app.SharedParametersFilename = previousFile ?? string.Empty;
			}
			catch (Exception)
			{
			}
		}

		return $"Parámetros: {created} creado(s), {extended} actualizado(s) en {categories.Size} categorías.";
	}

	/// <summary>Permite que cada copia de un grupo tenga su propia partida (si no, Revit no deja escribir en elementos agrupados).</summary>
	private static void AllowVaryBetweenGroups(Document doc, IList<string> names)
	{
		foreach (string name in names)
		{
			try
			{
				InternalDefinition definition = SharedParameterElement.Lookup(doc, GuidFor(name))?.GetDefinition();
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

	/// <summary>Los parámetros de nivel del elemento (null en los niveles que no tiene).</summary>
	public static Parameter[] Get(Element element, IList<string> names, IList<Guid> guids)
	{
		var parameters = new Parameter[names.Count];
		for (int i = 0; i < names.Count; i++)
		{
			parameters[i] = element.get_Parameter(guids[i]) ?? element.LookupParameter(names[i]);
		}

		return parameters;
	}
}
