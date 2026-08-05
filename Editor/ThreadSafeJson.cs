using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// The smallest JSON reader and writer that will carry a bridge request and response, for use on
    /// a thread that is not Unity's.
    ///
    /// This exists because <c>JsonUtility</c> is main-thread only, and the whole point of the off-thread
    /// path is to work while the main thread is stuck inside a modal dialog's message loop. A request
    /// read with JsonUtility on the watcher thread would be exactly the bug being fixed.
    ///
    /// It is deliberately not a general JSON library. It reads objects, strings, numbers, booleans,
    /// null and nested objects, which is the whole of what the MCP server emits, and it flattens
    /// everything to strings because <see cref="CommandParameters"/> is read field by field anyway.
    /// Arrays are parsed and skipped rather than kept: no command on the off-thread whitelist takes one.
    /// </summary>
    internal static class ThreadSafeJson
    {
        // ------------------------------------------------------------------ reading

        /// <summary>
        /// Flattens a JSON object into string values. Nested objects are prefixed with the parent key
        /// and a dot, so {"parameters":{"armMs":5}} reads back as "parameters.armMs" = "5".
        /// </summary>
        internal static Dictionary<string, string> Flatten(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }

            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{')
            {
                throw new FormatException("Expected a JSON object at the start of the request.");
            }

            ReadObject(json, ref index, string.Empty, result);
            return result;
        }

        private static void ReadObject(string json, ref int index, string prefix, Dictionary<string, string> into)
        {
            Expect(json, ref index, '{');
            SkipWhitespace(json, ref index);

            if (Peek(json, index) == '}')
            {
                index++;
                return;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                var key = ReadString(json, ref index);
                SkipWhitespace(json, ref index);
                Expect(json, ref index, ':');
                SkipWhitespace(json, ref index);

                var fullKey = prefix.Length == 0 ? key : prefix + "." + key;
                ReadValue(json, ref index, fullKey, into);

                SkipWhitespace(json, ref index);
                var next = Peek(json, index);
                if (next == ',')
                {
                    index++;
                    continue;
                }

                Expect(json, ref index, '}');
                return;
            }
        }

        private static void ReadValue(string json, ref int index, string key, Dictionary<string, string> into)
        {
            var c = Peek(json, index);
            switch (c)
            {
                case '{':
                    ReadObject(json, ref index, key, into);
                    return;

                case '[':
                    SkipArray(json, ref index);
                    return;

                case '"':
                    into[key] = ReadString(json, ref index);
                    return;

                default:
                {
                    var start = index;
                    while (index < json.Length && ",}] \t\r\n".IndexOf(json[index]) < 0)
                    {
                        index++;
                    }

                    var literal = json.Substring(start, index - start);
                    if (literal != "null")
                    {
                        into[key] = literal;
                    }
                    return;
                }
            }
        }

        private static void SkipArray(string json, ref int index)
        {
            Expect(json, ref index, '[');
            var depth = 1;
            while (index < json.Length && depth > 0)
            {
                var c = json[index];
                if (c == '"')
                {
                    ReadString(json, ref index);
                    continue;
                }

                if (c == '[') depth++;
                else if (c == ']') depth--;
                index++;
            }
        }

        private static string ReadString(string json, ref int index)
        {
            Expect(json, ref index, '"');
            var sb = new StringBuilder();

            while (index < json.Length)
            {
                var c = json[index++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (index >= json.Length) break;
                var escape = json[index++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (index + 4 <= json.Length)
                        {
                            var hex = json.Substring(index, 4);
                            index += 4;
                            int code;
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                            }
                        }
                        break;
                    default: sb.Append(escape); break;
                }
            }

            throw new FormatException("Unterminated string in JSON.");
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }

        private static char Peek(string json, int index)
        {
            return index < json.Length ? json[index] : '\0';
        }

        private static void Expect(string json, ref int index, char expected)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != expected)
            {
                throw new FormatException(
                    "Expected '" + expected + "' at offset " + index.ToString(CultureInfo.InvariantCulture) + ".");
            }

            index++;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Writes the response shape the MCP server reads. Kept identical to what JsonUtility produces
        /// for CommandResponse - including the always-present empty error object - so a caller cannot
        /// tell which thread answered it.
        /// </summary>
        internal static string WriteResponse(string id, string command, bool success, long elapsedMs,
            List<string> logs, List<KeyValuePair<string, string>> outputs, string errorMessage, string errorDetails)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("    \"id\": ").Append(Quote(id)).Append(",\n");
            sb.Append("    \"command\": ").Append(Quote(command)).Append(",\n");
            sb.Append("    \"success\": ").Append(success ? "true" : "false").Append(",\n");
            sb.Append("    \"elapsedMs\": ").Append(elapsedMs.ToString(CultureInfo.InvariantCulture)).Append(",\n");

            sb.Append("    \"logs\": [");
            if (logs != null)
            {
                for (var i = 0; i < logs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("\n        ").Append(Quote(logs[i]));
                }
                if (logs.Count > 0) sb.Append("\n    ");
            }
            sb.Append("],\n");

            sb.Append("    \"outputs\": [");
            if (outputs != null)
            {
                for (var i = 0; i < outputs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("\n        {\n            \"key\": ").Append(Quote(outputs[i].Key));
                    sb.Append(",\n            \"value\": ").Append(Quote(outputs[i].Value));
                    sb.Append("\n        }");
                }
                if (outputs.Count > 0) sb.Append("\n    ");
            }
            sb.Append("],\n");

            sb.Append("    \"error\": {\n");
            sb.Append("        \"message\": ").Append(Quote(errorMessage ?? string.Empty)).Append(",\n");
            sb.Append("        \"details\": ").Append(Quote(errorDetails ?? string.Empty)).Append('\n');
            sb.Append("    }\n");
            sb.Append('}');

            return sb.ToString();
        }

        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            if (!string.IsNullOrEmpty(value))
            {
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }

            return sb.Append('"').ToString();
        }
    }
}
