using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

internal static class DwgTextImporter
{
	internal class DwgTextEntry
	{
		public string Text;

		public XYZ Position;

		public double HeightFeet;

		public double RotationRadians;

		public TextAnchorH AnchorH;

		public TextAnchorV AnchorV;

		public double WidthFactor = 1.0;

		public string FontName;

		public bool Bold;
	}

	internal enum ReadStatus
	{
		Ok,
		NotLinked,
		FileNotFound,
		ReadError
	}

	/// <summary>
	/// DWG ya leídos por ruta de archivo durante una ejecución del comando, para no volver a
	/// parsear el mismo archivo cuando hay varias instancias del mismo vínculo.
	/// </summary>
	internal sealed class FileCache
	{
		internal sealed class Entry
		{
			public CadDocument Cad;

			public List<RawText> Texts;

			public double FeetPerUnit;
		}

		private readonly Dictionary<string, Entry> _byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<ElementId, Entry> _byInstance = new Dictionary<ElementId, Entry>();

		public bool TryGet(string path, out Entry entry) => _byPath.TryGetValue(path, out entry);

		public void Add(string path, Entry entry) => _byPath[path] = entry;

		/// <summary>Registra qué DWG leído corresponde a cada instancia (para leer después sus tipos de línea).</summary>
		public void Bind(ElementId instanceId, Entry entry) => _byInstance[instanceId] = entry;

		public Entry ForInstance(ElementId instanceId) => _byInstance.TryGetValue(instanceId, out Entry entry) ? entry : null;
	}

	public static ReadStatus TryReadTexts(Document doc, ImportInstance importInstance, FileCache cache,
		out List<DwgTextEntry> texts, string explicitFilePath = null)
	{
		texts = new List<DwgTextEntry>();
		string path = explicitFilePath;
		if (path == null)
		{
			if (!importInstance.IsLinked)
			{
				return ReadStatus.NotLinked;
			}

			ExternalFileReference fileRef = doc.GetElement(importInstance.GetTypeId())?.GetExternalFileReference();
			if (fileRef == null)
			{
				return ReadStatus.NotLinked;
			}

			try
			{
				path = ModelPathUtils.ConvertModelPathToUserVisiblePath(fileRef.GetAbsolutePath());
			}
			catch (Exception)
			{
				return ReadStatus.FileNotFound;
			}
		}

		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return ReadStatus.FileNotFound;
		}

		if (!cache.TryGet(path, out FileCache.Entry entry))
		{
			CadDocument cadDocument;
			try
			{
				cadDocument = ReadDwg(path);
			}
			catch (Exception)
			{
				return ReadStatus.ReadError;
			}

			if (cadDocument == null)
			{
				return ReadStatus.ReadError;
			}

			entry = new FileCache.Entry
			{
				Cad = cadDocument,
				Texts = CadTextCollector.Collect(cadDocument),
				FeetPerUnit = GetFeetPerDwgUnit(cadDocument.Header?.InsUnits ?? UnitsType.Unitless)
			};
			cache.Add(path, entry);
		}

		cache.Bind(importInstance.Id, entry);
		double feetPerUnit = entry.FeetPerUnit;

		Transform transform = importInstance.GetTransform();
		double basisAngle = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
		double scale = feetPerUnit * transform.Scale;
		foreach (RawText raw in entry.Texts)
		{
			var local = new XYZ(raw.X * feetPerUnit, raw.Y * feetPerUnit, raw.Z * feetPerUnit);
			texts.Add(new DwgTextEntry
			{
				Text = raw.Text,
				Position = transform.OfPoint(local),
				HeightFeet = raw.Height * scale,
				RotationRadians = raw.Rotation + basisAngle,
				AnchorH = raw.AnchorH,
				AnchorV = raw.AnchorV,
				WidthFactor = raw.WidthFactor,
				FontName = raw.FontName,
				Bold = raw.Bold
			});
		}

		return ReadStatus.Ok;
	}

	internal static CadDocument ReadDwg(string path)
	{
		using var reader = new DwgReader(path);
		// No necesitamos la información de resumen ni validar CRC: lectura más rápida y tolerante.
		reader.Configuration.CrcCheck = false;
		reader.Configuration.ReadSummaryInfo = false;
		return reader.Read();
	}

	internal static double GetFeetPerDwgUnit(UnitsType units)
	{
		return units switch
		{
			UnitsType.Millimeters => 0.0032808398950131233,
			UnitsType.Centimeters => 25.0 / 762.0,
			UnitsType.Decimeters => 125.0 / 381.0,
			UnitsType.Meters => 3.280839895013123,
			UnitsType.Kilometers => 3280.839895013123,
			UnitsType.Inches => 1.0 / 12.0,
			UnitsType.Feet => 1.0,
			UnitsType.Yards => 3.0,
			UnitsType.Miles => 5280.0,
			UnitsType.USSurveyFeet => 1.000002,
			UnitsType.USSurveyInches => 0.0833335,
			UnitsType.USSurveyYards => 3.000006,
			UnitsType.USSurveyMiles => 5280.010560000001,
			_ => 0.0032808398950131233,
		};
	}
}
