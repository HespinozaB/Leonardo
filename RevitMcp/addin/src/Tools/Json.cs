using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMcp.Tools;

internal static class Json
{
	public static readonly JsonSerializerOptions Options = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
	};

	public static JsonNode ToNode(object value) => value switch
	{
		null => null,
		JsonNode node => node,
		_ => JsonSerializer.SerializeToNode(value, value.GetType(), Options)
	};
}
