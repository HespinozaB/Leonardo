using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
		private readonly Dictionary<ElementId, double> _feetPerUnitByInstance = new Dictionary<ElementId, double>();

		public void Bind(ElementId instanceId, Entry entry, double feetPerUnit)
		{
			_byInstance[instanceId] = entry;
			_feetPerUnitByInstance[instanceId] = feetPerUnit;
		}

		public Entry ForInstance(ElementId instanceId) => _byInstance.TryGetValue(instanceId, out Entry entry) ? entry : null;

		/// <summary>Pies por unidad del DWG ya calibrados para esa instancia (según cómo se insertó en Revit).</summary>
		public double FeetPerUnitFor(ElementId instanceId) =>
			_feetPerUnitByInstance.TryGetValue(instanceId, out double f) ? f : ForInstance(instanceId)?.FeetPerUnit ?? 1.0;
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

		double feetPerUnit = CalibrateUnits(entry, importInstance);
		cache.Bind(importInstance.Id, entry, feetPerUnit);

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

	private static readonly double[] CandidateFeetPerUnit =
	{
		0.0032808398950131233, // mm
		25.0 / 762.0,          // cm
		125.0 / 381.0,         // dm
		3.280839895013123,     // m
		1.0 / 12.0,            // in
		1.0                    // ft
	};

	/// <summary>
	/// Revit puede haber insertado el DWG con unidades distintas a las declaradas en su cabecera (INSUNITS),
	/// p.ej. un DWG "sin unidades" importado en metros. Se prueba cada unidad y se elige la que hace que la
	/// geometría del DWG caiga sobre la instancia de Revit; ante empate, la declarada.
	/// </summary>
	private static double CalibrateUnits(FileCache.Entry entry, ImportInstance importInstance)
	{
		double declared = entry.FeetPerUnit;
		try
		{
			BoundingBoxXYZ bbox = importInstance.get_BoundingBox(null);
			List<ACadSharp.Entities.Entity> entities = entry.Cad?.Entities?.ToList();
			if (bbox == null || entities == null || entities.Count == 0)
			{
				return declared;
			}

			var samples = new List<CSMath.XYZ>();
			int step = Math.Max(1, entities.Count / 1500);
			for (int i = 0; i < entities.Count; i += step)
			{
				switch (entities[i])
				{
					case ACadSharp.Entities.Line line:
						samples.Add(line.StartPoint);
						samples.Add(line.EndPoint);
						break;
					case ACadSharp.Entities.Circle circle:
						samples.Add(circle.Center);
						break;
					case ACadSharp.Entities.Insert insert:
						samples.Add(insert.InsertPoint);
						break;
					case ACadSharp.Entities.LwPolyline poly when poly.Vertices.Count > 0:
						samples.Add(new CSMath.XYZ(poly.Vertices[0].Location.X, poly.Vertices[0].Location.Y, 0));
						break;
					case ACadSharp.Entities.TextEntity text:
						samples.Add(text.InsertPoint);
						break;
				}
			}

			if (samples.Count < 4)
			{
				return declared;
			}

			Transform transform = importInstance.GetTransform();
			double width = bbox.Max.X - bbox.Min.X;
			double height = bbox.Max.Y - bbox.Min.Y;
			double mx = width * 0.02 + 0.01, my = height * 0.02 + 0.01;
			double Score(double factor)
			{
				int inside = 0;
				double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
				foreach (CSMath.XYZ p in samples)
				{
					XYZ w = transform.OfPoint(new XYZ(p.X * factor, p.Y * factor, p.Z * factor));
					if (w.X >= bbox.Min.X - mx && w.X <= bbox.Max.X + mx && w.Y >= bbox.Min.Y - my && w.Y <= bbox.Max.Y + my)
					{
						inside++;
						minX = Math.Min(minX, w.X);
						maxX = Math.Max(maxX, w.X);
						minY = Math.Min(minY, w.Y);
						maxY = Math.Max(maxY, w.Y);
					}
				}

				if (inside == 0)
				{
					return 0.0;
				}

				// Además de caer dentro, el dibujo debe ocupar la caja (una escala demasiado pequeña
				// amontonaría todo en un punto y también "caería dentro").
				double coverage = Math.Min(1.0, Math.Max(width > 1E-06 ? (maxX - minX) / width : 0.0, height > 1E-06 ? (maxY - minY) / height : 0.0));
				return (double)inside / samples.Count * coverage;
			}

			double best = declared;
			double bestScore = Score(declared);
			foreach (double candidate in CandidateFeetPerUnit)
			{
				double score = Score(candidate);
				if (score > bestScore + 0.05)
				{
					best = candidate;
					bestScore = score;
				}
			}

			return best;
		}
		catch (Exception)
		{
			return declared;
		}
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
