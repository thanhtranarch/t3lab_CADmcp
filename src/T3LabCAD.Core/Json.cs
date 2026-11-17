using System;
using System.Collections.Generic;
using System.Globalization;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Encodings.Web;
using System.Text.Json;
#endif

namespace T3LabCAD.Core
{
    /// <summary>
    /// JSON with no extra DLL next to AutoCAD: JavaScriptSerializer on .NET Framework (AutoCAD 2022-2024),
    /// System.Text.Json on .NET 8+ (2025+). Both give the same shapes: objects are
    /// Dictionary&lt;string, object&gt;, arrays are object[], numbers are int/long/decimal (double past decimal).
    /// </summary>
    internal static class Json
    {
#if NETFRAMEWORK
        static JavaScriptSerializer New() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static object Parse(string text) => New().DeserializeObject(text);

        public static string Write(object value) => New().Serialize(value);
#else
        static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            MaxDepth = 512,
            IncludeFields = true,                   // JavaScriptSerializer wrote public fields too
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static object Parse(string text)
        {
            using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 }))
                return Convert(doc.RootElement);
        }

        public static string Write(object value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), WriteOptions);

        static object Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = Convert(p.Value);
                    return d;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in e.EnumerateArray()) list.Add(Convert(item));
                    return list.ToArray();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number:
                    if (e.TryGetInt32(out int i)) return i;
                    if (e.TryGetInt64(out long l)) return l;
                    if (e.TryGetDecimal(out decimal m)) return m;
                    return e.GetDouble();
                default: return null;
            }
        }
#endif

        /// <summary>Tool schemas are written in C# with single quotes for readability.</summary>
        public static Dictionary<string, object> Schema(string singleQuoted) =>
            (Dictionary<string, object>)Parse(singleQuoted.Replace('\'', '"'));
    }
}
