using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitParamFill.Core;

/// <summary>De dónde sale el valor de un parámetro.</summary>
public enum SourceKind
{
	Categoria,
	Familia,
	Tipo,
	FamiliaTipo,
	Marca,
	MarcaTipo,
	Fabricante,
	Modelo,
	Nivel,
	Material,
	Parametro,
	Fijo,
	Sector,
	Libre,
	NoAplica
}

/// <summary>Una regla: cómo se llena un parámetro destino y qué hacer si no hay dato.</summary>
[DataContract]
public sealed class FillRule
{
	public static readonly (SourceKind Kind, string Text)[] Sources =
	{
		(SourceKind.Categoria, "Categoría"),
		(SourceKind.Familia, "Familia"),
		(SourceKind.Tipo, "Nombre de tipo"),
		(SourceKind.FamiliaTipo, "Familia : Tipo"),
		(SourceKind.Marca, "Marca"),
		(SourceKind.MarcaTipo, "Marca de tipo"),
		(SourceKind.Fabricante, "Fabricante"),
		(SourceKind.Modelo, "Modelo"),
		(SourceKind.Nivel, "Nivel"),
		(SourceKind.Material, "Material"),
		(SourceKind.Parametro, "Parámetro"),
		(SourceKind.Fijo, "Valor fijo"),
		(SourceKind.Sector, "Sectorización"),
		(SourceKind.Libre, "Libre (masivo / personalizado)"),
		(SourceKind.NoAplica, "N/A (no se toca)")
	};

	public static string SourceText(SourceKind kind) => Sources.First(s => s.Kind == kind).Text;

	public static SourceKind SourceOf(string text) => Sources.Where(s => s.Text == text).Select(s => (SourceKind?)s.Kind).FirstOrDefault() ?? SourceKind.NoAplica;

	[DataMember(Order = 1)]
	public string Target = string.Empty;

	[DataMember(Order = 2)]
	public SourceKind Source = SourceKind.NoAplica;

	/// <summary>Parámetro de origen (Parámetro), valor (Fijo) o nombre del conjunto de sectores (Sectorización).</summary>
	[DataMember(Order = 3)]
	public string Detail = string.Empty;

	[DataMember(Order = 4)]
	public string Prefix = string.Empty;

	[DataMember(Order = 5)]
	public string Suffix = string.Empty;

	/// <summary>Equivalencias: valor leído (simplificado) → valor a escribir (p. ej. "muros" → "MURO").</summary>
	[DataMember(Order = 6)]
	public Dictionary<string, string> ValueMap = new Dictionary<string, string>(StringComparer.Ordinal);

	// Deducción cuando no hay dato (se propone y el usuario la confirma).
	[DataMember(Order = 7)]
	public bool InferSameType = true;

	[DataMember(Order = 8)]
	public bool InferNearest = true;

	[DataMember(Order = 9)]
	public bool InferKeywords = true;

	/// <summary>Palabra clave (en familia, tipo o categoría) → valor.</summary>
	[DataMember(Order = 10)]
	public Dictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal);

	[DataMember(Order = 11)]
	public string Default = string.Empty;

	/// <summary>Sobrescribir los valores que ya tienen los elementos.</summary>
	[DataMember(Order = 12)]
	public bool Overwrite;

	[DataMember(Order = 13)]
	public bool Enabled = true;

	public bool IsAutomatic => Enabled && Source != SourceKind.Libre && Source != SourceKind.NoAplica && Target.Length > 0;

	/// <summary>Aplica el mapa de valores y el prefijo/sufijo a un valor leído.</summary>
	public string Transform(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		value = value.Trim();
		if (ValueMap.TryGetValue(Text.Simplify(value), out string mapped))
		{
			value = mapped;
		}

		return Prefix + value + Suffix;
	}

	public FillRule Clone()
	{
		var copy = (FillRule)MemberwiseClone();
		copy.ValueMap = new Dictionary<string, string>(ValueMap, StringComparer.Ordinal);
		copy.Keywords = new Dictionary<string, string>(Keywords, StringComparer.Ordinal);
		return copy;
	}
}

/// <summary>Origen de un conjunto de sectores.</summary>
public enum SectorSource
{
	/// <summary>Regiones rellenas o lazos de líneas de detalle en una vista de planta.</summary>
	Trazo,

	/// <summary>Polilíneas cerradas de una capa de un DWG vinculado o importado.</summary>
	Dwg
}

/// <summary>De dónde sale el nombre de cada sector.</summary>
public enum SectorName
{
	/// <summary>El texto que cae dentro (TextNote en Revit o texto del DWG).</summary>
	Texto,

	/// <summary>El nombre del tipo de región rellena (o la capa del DWG).</summary>
	Tipo,

	/// <summary>El parámetro Comentarios de la región rellena.</summary>
	Comentarios,

	/// <summary>Cualquier parámetro de la región rellena (ejemplar o tipo): Marca, un código, etc.</summary>
	Parametro
}

/// <summary>Un conjunto de sectores (p. ej. "Edificio" o "Local") dibujado en una planta.</summary>
[DataContract]
public sealed class SectorSet
{
	[DataMember(Order = 1)]
	public string Name = "Sectores";

	[DataMember(Order = 2)]
	public SectorSource Source = SectorSource.Trazo;

	/// <summary>Vista de planta donde está el trazo o el DWG.</summary>
	[DataMember(Order = 3)]
	public long ViewId = -1;

	/// <summary>Trazo: tipo de región rellena o estilo de línea ("" = todos).</summary>
	[DataMember(Order = 4)]
	public string Filter = string.Empty;

	/// <summary>Trazo: true = regiones rellenas; false = lazos de líneas de detalle.</summary>
	[DataMember(Order = 5)]
	public bool UseFilledRegions = true;

	[DataMember(Order = 6)]
	public SectorName NameFrom = SectorName.Texto;

	/// <summary>DWG: instancia (vínculo o importación) y capa.</summary>
	[DataMember(Order = 7)]
	public long DwgInstanceId = -1;

	[DataMember(Order = 8)]
	public string Layer = string.Empty;

	/// <summary>DWG importado (no vinculado): ruta del archivo para leer sus textos.</summary>
	[DataMember(Order = 9)]
	public string DwgPath = string.Empty;

	/// <summary>Solo los elementos del nivel de la vista (si no, el sector vale para todos los niveles).</summary>
	[DataMember(Order = 10)]
	public bool OnlyViewLevel;

	/// <summary>NameParameter para la Marca de la región (se lee por su parámetro interno, en cualquier idioma de Revit).</summary>
	public const string MarkToken = "@MARCA";

	/// <summary>Para NameFrom = Parametro: el parámetro de la región que da el nombre (p. ej. "@MARCA" o "SUNAT_Código").</summary>
	[DataMember(Order = 11)]
	public string NameParameter = string.Empty;

	/// <summary>
	/// false (por defecto): si un elemento cruza varios sectores, se asigna el que contiene la mayor parte de su área (o longitud).
	/// true: solo cuenta el punto central del elemento.
	/// </summary>
	[DataMember(Order = 12)]
	public bool CenterOnly;

	/// <summary>Sectores leídos (no se guardan: se leen del modelo).</summary>
	public List<Polygon> Sectors = new List<Polygon>();

	public SectorSet Clone()
	{
		var copy = (SectorSet)MemberwiseClone();
		copy.Sectors = new List<Polygon>(Sectors);
		return copy;
	}
}

/// <summary>Reglas y sectores (se guardan y cargan como JSON).</summary>
[DataContract]
public sealed class RuleSet
{
	[DataMember(Order = 1)]
	public List<FillRule> Rules = new List<FillRule>();

	[DataMember(Order = 2)]
	public List<SectorSet> SectorSets = new List<SectorSet>();

	public static string DefaultPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "Rellenar - reglas.json");

	/// <summary>Tabla 5 de SUNAT (PEB): los 10 parámetros con el origen indicado.</summary>
	public static RuleSet Sunat()
	{
		var set = new RuleSet();
		void Add(string target, SourceKind source, string detail = "")
		{
			set.Rules.Add(new FillRule { Target = target, Source = source, Detail = detail });
		}

		Add("SUNAT_Categoría de Familia", SourceKind.Categoria);
		Add("SUNAT_Tipo de Elemento", SourceKind.Tipo);
		Add("SUNAT_Nivel de Elemento", SourceKind.Nivel);
		Add("SUNAT_Material", SourceKind.Material);
		Add("SUNAT_Código de Elemento", SourceKind.Sector, "Local");
		Add("SUNAT_Código Sitio", SourceKind.NoAplica);
		Add("SUNAT_Código Edificio", SourceKind.Sector, "Edificio");
		Add("SUNAT_Código Unidad Operativa", SourceKind.Libre);
		Add("SUNAT_Especialidad", SourceKind.Parametro);
		Add("SUNAT_Sistema", SourceKind.Fijo, "General");
		set.SectorSets.Add(new SectorSet { Name = "Edificio" });
		set.SectorSets.Add(new SectorSet { Name = "Local" });
		return set;
	}

	public string ToJson()
	{
		var serializer = new DataContractJsonSerializer(typeof(RuleSet), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
		using var stream = new MemoryStream();
		serializer.WriteObject(stream, this);
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	public static RuleSet FromJson(string json)
	{
		var serializer = new DataContractJsonSerializer(typeof(RuleSet), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
		var set = (RuleSet)serializer.ReadObject(stream);
		// Los campos sin valor en el JSON quedan en null al deserializar.
		foreach (FillRule rule in set.Rules)
		{
			rule.Target ??= string.Empty;
			rule.Detail ??= string.Empty;
			rule.Prefix ??= string.Empty;
			rule.Suffix ??= string.Empty;
			rule.Default ??= string.Empty;
			rule.ValueMap = new Dictionary<string, string>(rule.ValueMap ?? new Dictionary<string, string>(), StringComparer.Ordinal);
			rule.Keywords = new Dictionary<string, string>(rule.Keywords ?? new Dictionary<string, string>(), StringComparer.Ordinal);
		}

		set.SectorSets ??= new List<SectorSet>();
		foreach (SectorSet sectors in set.SectorSets)
		{
			sectors.Name ??= "Sectores";
			sectors.Filter ??= string.Empty;
			sectors.Layer ??= string.Empty;
			sectors.DwgPath ??= string.Empty;
			sectors.NameParameter ??= string.Empty;
			sectors.Sectors = new List<Polygon>();
		}

		return set;
	}

	public void Save(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllText(path, ToJson(), Encoding.UTF8);
	}

	public static RuleSet Load(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));
}
