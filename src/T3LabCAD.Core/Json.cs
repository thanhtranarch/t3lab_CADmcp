using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace T3LabCAD.Core
{
    /// <summary>
    /// JSON via the framework's JavaScriptSerializer (no extra DLL to ship next to AutoCAD).
    /// Parsed objects are Dictionary&lt;string, object&gt;, arrays are object[], numbers are int/long/decimal.
    /// </summary>
    internal static class Json
    {
        static JavaScriptSerializer New() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static object Parse(string text) => New().DeserializeObject(text);

        public static string Write(object value) => New().Serialize(value);

        /// <summary>Tool schemas are written in C# with single quotes for readability.</summary>
        public static Dictionary<string, object> Schema(string singleQuoted) =>
            (Dictionary<string, object>)Parse(singleQuoted.Replace('\'', '"'));
    }
}
