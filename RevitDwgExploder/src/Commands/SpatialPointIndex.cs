using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Índice por celdas (en planta XY) para responder "¿hay un punto a menos de d?" en O(1)
/// en lugar de recorrer toda la lista para cada consulta.
/// </summary>
internal sealed class SpatialPointIndex
{
	private readonly double _cellSize;

	private readonly Dictionary<(long, long), List<XYZ>> _cells = new Dictionary<(long, long), List<XYZ>>();

	public SpatialPointIndex(IEnumerable<XYZ> points, double cellSize)
	{
		_cellSize = Math.Max(cellSize, 1E-06);
		if (points == null)
		{
			return;
		}

		foreach (XYZ p in points)
		{
			Add(p);
		}
	}

	public void Add(XYZ point)
	{
		var key = KeyOf(point.X, point.Y);
		if (!_cells.TryGetValue(key, out List<XYZ> list))
		{
			list = new List<XYZ>();
			_cells[key] = list;
		}

		list.Add(point);
	}

	public bool Contains(XYZ point, double distance)
	{
		var (cx, cy) = KeyOf(point.X, point.Y);
		long range = Math.Max(1L, (long)Math.Ceiling(distance / _cellSize));
		for (long ix = cx - range; ix <= cx + range; ix++)
		{
			for (long iy = cy - range; iy <= cy + range; iy++)
			{
				if (!_cells.TryGetValue((ix, iy), out List<XYZ> list))
				{
					continue;
				}

				foreach (XYZ p in list)
				{
					double dx = p.X - point.X;
					double dy = p.Y - point.Y;
					if (dx * dx + dy * dy < distance * distance)
					{
						return true;
					}
				}
			}
		}

		return false;
	}

	private (long, long) KeyOf(double x, double y) =>
		((long)Math.Floor(x / _cellSize), (long)Math.Floor(y / _cellSize));
}
