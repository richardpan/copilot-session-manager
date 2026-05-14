using System;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services;

/// <summary>
/// WPF implementation of <see cref="IClipboardService"/> backed by
/// <see cref="Clipboard.SetText(string)"/>. The clipboard is a
/// globally-locked resource so the call can throw transient
/// <c>COMException</c> / <c>ExternalException</c>; we log and rethrow so
/// the calling view-model can surface a toast.
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    private readonly ILogger<WpfClipboardService> _logger;

    public WpfClipboardService(ILogger<WpfClipboardService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy {Length} chars to the clipboard.", text.Length);
            throw;
        }
    }
}
