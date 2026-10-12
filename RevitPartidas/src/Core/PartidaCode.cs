using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitPartidas.Core;

/// <summary>
/// Códigos de partida del presupuesto: "03", "03.01", … ("03.01.04.02.01.01" tiene 6 niveles; se admiten hasta 8).
/// Los códigos de especificación ("OE.3.2.2.1") no son partidas y se descartan.
/// </summary>
public static class PartidaCode
{
	public const int MaxLevels = 8;

	private static readonly Regex Pattern = new Regex(@"^\d{2}(\.\d{2}){0,7}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>Limpia espacios (también NBSP), comas y puntos sobrantes; devuelve null si no es un código de partida.</summary>
	public static string Normalize(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		string text = value.Replace(' ', ' ').Trim().Replace(',', '.').Replace(" ", string.Empty).Trim('.');
		if (text.Length == 0 || !char.IsDigit(text[0]))
		{
			return null;
		}

		return Pattern.IsMatch(text) ? text : null;
	}

	public static int Depth(string code) => string.IsNullOrEmpty(code) ? 0 : code.Split('.').Length;

	/// <summary>Los códigos de cada nivel, del 1 al <paramref name="count"/> ("" en los niveles que no aplican).</summary>
	public static string[] Levels(string code, int count)
	{
		var levels = new string[count];
		for (int i = 0; i < count; i++)
		{
			levels[i] = string.Empty;
		}

		if (string.IsNullOrEmpty(code))
		{
			return levels;
		}

		string[] parts = code.Split('.');
		for (int i = 0; i < parts.Length && i < count; i++)
		{
			levels[i] = string.Join(".", parts, 0, i + 1);
		}

		return levels;
	}

	/// <summary>El prefijo del código hasta el nivel indicado (1 = "03"); "" si el código es más corto.</summary>
	public static string Prefix(string code, int level)
	{
		if (string.IsNullOrEmpty(code) || level < 1)
		{
			return string.Empty;
		}

		string[] parts = code.Split('.');
		return parts.Length < level ? string.Empty : string.Join(".", parts, 0, level);
	}

	/// <summary>El código más profundo escrito en los 6 niveles (null si están vacíos o no son coherentes entre sí).</summary>
	public static string Deepest(IReadOnlyList<string> levels, out bool consistent)
	{
		consistent = true;
		string deepest = null;
		for (int i = levels.Count - 1; i >= 0; i--)
		{
			string code = Normalize(levels[i]);
			if (code == null)
			{
				continue;
			}

			if (deepest == null)
			{
				deepest = code;
			}
			else if (!IsAncestorOrSelf(code, deepest))
			{
				consistent = false;
			}
		}

		return deepest;
	}

	public static bool IsAncestorOrSelf(string ancestor, string code) =>
		code == ancestor || code.StartsWith(ancestor + ".", StringComparison.Ordinal);

	/// <summary>Orden natural de códigos (03.01.10 después de 03.01.09).</summary>
	public static int Compare(string a, string b) => string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
}
