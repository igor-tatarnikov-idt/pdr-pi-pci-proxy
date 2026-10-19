using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sandbox.Shared;

/// <summary>
/// The readable console format (<c>Logging:Console:FormatterName</c> <c>sandbox-text</c>), one line per entry: <c>HH:mm:ss Category id=&lt;request id&gt; trace=&lt;trace id&gt; message</c>. No level and
/// no event id (the stock formatter's <c>info:</c> and <c>[0]</c>). The request id (<see cref="RequestId"/>) and the W3C trace id
/// (the current <see cref="Activity"/>, which ASP.NET Core starts per request and HttpClient passes on in <c>traceparent</c>) are
/// read from the request being handled, so every line written while handling one has them, the framework's included; a line
/// outside a request (startup) has neither. Line breaks in the message become <c>\n</c>, so that one entry is one line.
/// </summary>
public sealed class SandboxTextFormatter() : ConsoleFormatter(Name)
{
    public const string Name = "sandbox-text";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (message is null && entry.Exception is null)
        {
            return;
        }

        textWriter.Write(DateTime.Now.ToString("HH:mm:ss"));
        textWriter.Write(' ');
        textWriter.Write(entry.Category);
        if (RequestId.Current is { } id)
        {
            textWriter.Write(" id=");
            textWriter.Write(id);
        }
        if (Activity.Current is { } activity && activity.TraceId != default)
        {
            textWriter.Write(" trace=");
            textWriter.Write(activity.TraceId.ToString());
        }
        if (message is not null)
        {
            textWriter.Write(' ');
            textWriter.Write(message.Replace("\r", "").Replace("\n", "\\n"));
        }
        textWriter.WriteLine();
        if (entry.Exception is not null)
        {
            textWriter.WriteLine(entry.Exception.ToString());
        }
    }
}
