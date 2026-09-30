using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace RevitMCP.Bridge;

internal static class Json
{
	private static JavaScriptSerializer NewSerializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

	public static Dictionary<string, object> ParseObject(string json)
	{
		return NewSerializer().Deserialize<Dictionary<string, object>>(json);
	}

	public static string Ok(object id, object result)
	{
		return NewSerializer().Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = true, ["result"] = result });
	}

	public static string Error(object id, string message)
	{
		return NewSerializer().Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = false, ["error"] = message });
	}
}
