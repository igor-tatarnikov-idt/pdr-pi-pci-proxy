using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sandbox.Shared;

/// <summary>
/// The sandbox's structured console format (<c>Logging:Console:FormatterName</c> <c>sandbox</c>): one JSON object per line.
/// <c>ts</c> (UTC), <c>category</c>, <c>requestId</c> and <c>traceId</c> (see <see cref="RequestId"/> and the current
/// <see cref="Activity"/>; both left out outside a request), <c>message</c>, then one field for each named value of the log call
/// (<c>{Origin}</c> becomes <c>origin</c>), then <c>exception</c> when there is one. <c>message</c> is the message template
/// (<c>"in from={Origin} ..."</c>), which is the same for every entry of a kind, so that entries group by it; the values are in the
/// fields, with their JSON types (a number stays a number, null stays null). A message with no named values is written as it is.
/// </summary>
public sealed class SandboxJsonFormatter() : ConsoleFormatter(Name)
{
    public const string Name = "sandbox";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Names this formatter writes itself; a value of the same name gets a trailing underscore.
    private static readonly HashSet<string> Reserved = ["ts", "category", "requestId", "traceId", "message", "exception"];

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var template = (entry.State as IEnumerable<KeyValuePair<string, object?>>)?
            .FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
        var message = template ?? entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (message is null && entry.Exception is null)
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
            json.WriteString("category", entry.Category);
            if (RequestId.Current is { } id)
            {
                json.WriteString("requestId", id);
            }
            if (Activity.Current is { } activity && activity.TraceId != default)
            {
                json.WriteString("traceId", activity.TraceId.ToString());
            }
            if (message is not null)
            {
                json.WriteString("message", message);
            }
            if (entry.State is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (name, value) in values)
                {
                    if (name != "{OriginalFormat}")
                    {
                        WriteValue(json, FieldName(name), value);
                    }
                }
            }
            if (entry.Exception is not null)
            {
                json.WriteString("exception", entry.Exception.ToString());
            }
            json.WriteEndObject();
        }
        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // {Origin} -> origin; a name that is one of the formatter's own gets an underscore.
    private static string FieldName(string name)
    {
        var field = name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;
        return Reserved.Contains(field) ? field + "_" : field;
    }

    private static void WriteValue(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(name); break;
            case string s: json.WriteString(name, s); break;
            case bool b: json.WriteBoolean(name, b); break;
            case byte or sbyte or short or ushort or int or uint or long: json.WriteNumber(name, Convert.ToInt64(value)); break;
            case ulong u: json.WriteNumber(name, u); break;
            case float f when float.IsFinite(f): json.WriteNumber(name, f); break;
            case double d when double.IsFinite(d): json.WriteNumber(name, d); break;
            case decimal m: json.WriteNumber(name, m); break;
            default: json.WriteString(name, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}

public static class SandboxLoggingExtensions
{
    /// <summary>
    /// Registers the sandbox console formatters: <c>sandbox</c> (JSON, one object per line) and <c>sandbox-text</c> (readable text).
    /// <c>Logging:Console:FormatterName</c> in appsettings.json (or <c>Logging__Console__FormatterName</c>) selects one.
    /// </summary>
    public static ILoggingBuilder AddSandboxConsoleFormatter(this ILoggingBuilder logging) =>
        logging.AddConsoleFormatter<SandboxJsonFormatter, ConsoleFormatterOptions>()
            .AddConsoleFormatter<SandboxTextFormatter, ConsoleFormatterOptions>();
}
