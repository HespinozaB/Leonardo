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
	}

	internal enum ReadStatus
	{
		Ok,
		NotLinked,
		FileNotFound,
		ReadError
	}

	/// <summary>
	/// Textos ya leídos por ruta de archivo durante una ejecución del comando, para no volver a
	/// parsear el mismo DWG cuando hay varias instancias del mismo vínculo.
	/// </summary>
	internal sealed class FileCache
	{
		private readonly Dictionary<string, (List<RawText> Texts, double FeetPerUnit)> _byPath =
			new Dictionary<string, (List<RawText>, double)>(StringComparer.OrdinalIgnoreCase);

		public bool TryGet(string path, out List<RawText> texts, out double feetPerUnit)
		{
			if (_byPath.TryGetValue(path, out var entry))
			{
				texts = entry.Texts;
				feetPerUnit = entry.FeetPerUnit;
				return true;
			}

			texts = null;
			feetPerUnit = 0.0;
			return false;
		}

		public void Add(string path, List<RawText> texts, double feetPerUnit)
		{
			_byPath[path] = (texts, feetPerUnit);
		}
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

		if (!cache.TryGet(path, out List<RawText> rawTexts, out double feetPerUnit))
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

			rawTexts = CadTextCollector.Collect(cadDocument);
			feetPerUnit = GetFeetPerDwgUnit(cadDocument.Header?.InsUnits ?? UnitsType.Unitless);
			cache.Add(path, rawTexts, feetPerUnit);
		}

		Transform transform = importInstance.GetTransform();
		double basisAngle = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
		double scale = feetPerUnit * transform.Scale;
		foreach (RawText raw in rawTexts)
		{
			var local = new XYZ(raw.X * feetPerUnit, raw.Y * feetPerUnit, raw.Z * feetPerUnit);
			texts.Add(new DwgTextEntry
			{
				Text = raw.Text,
				Position = transform.OfPoint(local),
				HeightFeet = raw.Height * scale,
				RotationRadians = raw.Rotation + basisAngle,
				AnchorH = raw.AnchorH,
				AnchorV = raw.AnchorV
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
