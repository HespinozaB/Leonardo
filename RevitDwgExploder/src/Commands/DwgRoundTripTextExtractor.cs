using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Obtiene el texto de un CAD importado reexportando la vista activa a DWG y leyendo el resultado.
/// La exportación (lo caro) se hace una sola vez por vista y se reutiliza para todas las instancias.
/// </summary>
internal sealed class DwgRoundTripTextExtractor : IDisposable
{
	private static readonly double[] CandidateScales =
	{
		1.0,
		1.0 / 12.0,
		0.0032808398950131233,
		25.0 / 762.0,
		3.280839895013123
	};

	private readonly Document _doc;

	private readonly View _view;

	private readonly string _tempDir;

	private bool _loaded;

	private List<RawText> _texts = new List<RawText>();

	private CadDocument _cadDoc;

	private SpatialPointIndex _existingTextNotes;

	public DwgRoundTripTextExtractor(Document doc, View view)
	{
		_doc = doc;
		_view = view;
		_tempDir = Path.Combine(Path.GetTempPath(), "RevitDwgExploder_" + Guid.NewGuid().ToString("N"));
	}

	public List<DwgTextImporter.DwgTextEntry> Extract(ImportInstance importInstance)
	{
		var result = new List<DwgTextImporter.DwgTextEntry>();
		try
		{
			BoundingBoxXYZ bbox = importInstance.get_BoundingBox(_view);
			if (bbox == null)
			{
				return result;
			}

			EnsureLoaded();
			if (_texts.Count == 0)
			{
				return result;
			}

			double scale = ResolveScale(bbox);
			if (scale <= 0.0)
			{
				return result;
			}

			double z = (bbox.Min.Z + bbox.Max.Z) / 2.0;
			foreach (RawText raw in _texts)
			{
				var position = new XYZ(raw.X * scale, raw.Y * scale, z);
				double height = raw.Height * scale;
				if (height < 0.0001 || !IsInside(position, bbox) || _existingTextNotes.Contains(position, 0.05))
				{
					continue;
				}

				result.Add(new DwgTextImporter.DwgTextEntry
				{
					Text = raw.Text,
					Position = position,
					HeightFeet = height,
					RotationRadians = raw.Rotation,
					AnchorH = raw.AnchorH,
					AnchorV = raw.AnchorV
				});
			}
		}
		catch (Exception)
		{
		}

		return result;
	}

	private void EnsureLoaded()
	{
		if (_loaded)
		{
			return;
		}

		_loaded = true;
		_existingTextNotes = new SpatialPointIndex(GetExistingTextNotePositions(), 0.05);
		try
		{
			Directory.CreateDirectory(_tempDir);
			_cadDoc = ExportAndRead(TextTreatment.Approximate);
			_texts = CadTextCollector.Collect(_cadDoc);
			if (_texts.Count == 0)
			{
				_cadDoc = ExportAndRead(TextTreatment.Exact);
				_texts = CadTextCollector.Collect(_cadDoc);
			}
		}
		catch (Exception)
		{
			_texts = new List<RawText>();
		}
	}

	private CadDocument ExportAndRead(TextTreatment textTreatment)
	{
		string folder = Path.Combine(_tempDir, textTreatment.ToString());
		Directory.CreateDirectory(folder);
		var options = new DWGExportOptions
		{
			MergedViews = true,
			TargetUnit = ExportUnit.Foot,
			SharedCoords = false,
			TextTreatment = textTreatment,
			FileVersion = ACADVersion.R2013
		};
		_doc.Export(folder, "roundtrip", new List<ElementId> { _view.Id }, options);
		string file = Directory.GetFiles(folder, "*.dwg").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
		if (file == null)
		{
			return null;
		}

		try
		{
			return DwgTextImporter.ReadDwg(file);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private double ResolveScale(BoundingBoxXYZ bbox)
	{
		var candidates = new List<double>(CandidateScales);
		if (_cadDoc?.Header != null)
		{
			double declared = DwgTextImporter.GetFeetPerDwgUnit(_cadDoc.Header.InsUnits);
			if (declared > 0.0 && !candidates.Any(c => Math.Abs(c - declared) < 1E-09))
			{
				candidates.Insert(0, declared);
			}
		}

		double best = 0.0;
		int bestCount = 0;
		foreach (double candidate in candidates)
		{
			int count = 0;
			foreach (RawText t in _texts)
			{
				if (IsInside(t.X * candidate, t.Y * candidate, bbox))
				{
					count++;
				}
			}

			if (count > bestCount)
			{
				bestCount = count;
				best = candidate;
			}
		}

		return bestCount > 0 ? best : 0.0;
	}

	private static bool IsInside(XYZ point, BoundingBoxXYZ bbox) => IsInside(point.X, point.Y, bbox);

	private static bool IsInside(double x, double y, BoundingBoxXYZ bbox)
	{
		double minX = Math.Min(bbox.Min.X, bbox.Max.X);
		double maxX = Math.Max(bbox.Min.X, bbox.Max.X);
		double minY = Math.Min(bbox.Min.Y, bbox.Max.Y);
		double maxY = Math.Max(bbox.Min.Y, bbox.Max.Y);
		double marginX = (maxX - minX) * 0.1;
		double marginY = (maxY - minY) * 0.1;
		return x >= minX - marginX && x <= maxX + marginX && y >= minY - marginY && y <= maxY + marginY;
	}

	private List<XYZ> GetExistingTextNotePositions()
	{
		try
		{
			return new FilteredElementCollector(_doc, _view.Id)
				.OfClass(typeof(TextNote))
				.Cast<TextNote>()
				.Select(t => t.Coord)
				.Where(c => c != null)
				.ToList();
		}
		catch (Exception)
		{
			return new List<XYZ>();
		}
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}
		catch (Exception)
		{
		}
	}
}
