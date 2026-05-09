using System.Collections.Generic;
using CopilotSessionManager.Core.Logging;
using Serilog.Core;
using Serilog.Events;

namespace CopilotSessionManager.Logging;

/// <summary>
/// Serilog enricher that runs every event through
/// <see cref="LogRedaction"/>. For string scalar properties it scrubs
/// known token shapes inline. For properties whose <em>name</em> is on the
/// sensitive list (e.g. <c>Prompt</c>, <c>Token</c>, <c>Authorization</c>) it
/// replaces the value entirely with <see cref="LogRedaction.Placeholder"/>
/// regardless of content. The <c>MessageTemplate</c> itself is not rewritten —
/// templates come from source code, not from data.
/// </summary>
public sealed class LogRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var replacements = new List<LogEventProperty>();

        foreach (var kvp in logEvent.Properties)
        {
            var name = kvp.Key;
            var value = kvp.Value;

            if (LogRedaction.IsSensitivePropertyName(name))
            {
                replacements.Add(new LogEventProperty(name, new ScalarValue(LogRedaction.Placeholder)));
                continue;
            }

            if (value is ScalarValue scalar && scalar.Value is string str)
            {
                var scrubbed = LogRedaction.Redact(str);
                if (!ReferenceEquals(scrubbed, str))
                {
                    replacements.Add(new LogEventProperty(name, new ScalarValue(scrubbed)));
                }
            }
        }

        foreach (var p in replacements)
        {
            logEvent.AddOrUpdateProperty(p);
        }
    }
}
