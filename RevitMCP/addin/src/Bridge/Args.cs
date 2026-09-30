using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMCP.Bridge;

/// <summary>Acceso tipado a los parámetros JSON de una petición.</summary>
internal sealed class Args
{
	private readonly Dictionary<string, object> _values;

	public Args(Dictionary<string, object> values)
	{
		_values = values ?? new Dictionary<string, object>();
	}

	public bool Has(string name) => _values.TryGetValue(name, out object v) && v != null;

	public object Raw(string name) => _values.TryGetValue(name, out object v) ? v : null;

	public string Str(string name, string fallback = null)
	{
		object v = Raw(name);
		return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture);
	}

	public string ReqStr(string name)
	{
		string v = Str(name);
		if (string.IsNullOrWhiteSpace(v))
		{
			throw new ArgumentException("Falta el parámetro obligatorio '" + name + "'.");
		}
		return v;
	}

	public double Num(string name, double fallback)
	{
		object v = Raw(name);
		return v == null ? fallback : ToDouble(v);
	}

	public double ReqNum(string name)
	{
		if (!Has(name))
		{
			throw new ArgumentException("Falta el parámetro obligatorio '" + name + "'.");
		}
		return ToDouble(Raw(name));
	}

	public int Int(string name, int fallback) => Has(name) ? (int)ToDouble(Raw(name)) : fallback;

	public bool Bool(string name, bool fallback)
	{
		object v = Raw(name);
		if (v == null)
		{
			return fallback;
		}
		if (v is bool b)
		{
			return b;
		}
		return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out bool parsed) ? parsed : fallback;
	}

	public List<long> Ids(string name)
	{
		object v = Raw(name);
		if (v == null)
		{
			return new List<long>();
		}
		if (v is IEnumerable list && !(v is string))
		{
			return list.Cast<object>().Select(o => (long)ToDouble(o)).ToList();
		}
		return new List<long> { (long)ToDouble(v) };
	}

	public List<object> List(string name)
	{
		object v = Raw(name);
		return v is IEnumerable list && !(v is string) ? list.Cast<object>().ToList() : new List<object>();
	}

	public static double ToDouble(object v)
	{
		if (v is string s)
		{
			return double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
		}
		return Convert.ToDouble(v, CultureInfo.InvariantCulture);
	}
}
