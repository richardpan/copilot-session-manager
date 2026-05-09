using System.Text.Json;

namespace CopilotSessionManager.Core.Models;

/// <summary>
/// A single event line from a session's <c>events.jsonl</c> stream.
/// </summary>
/// <param name="Id">The event's unique id.</param>
/// <param name="Type">The event type string (e.g. <c>session.start</c>, <c>assistant.turn_start</c>).</param>
/// <param name="Timestamp">The event's UTC timestamp.</param>
/// <param name="ParentId">The parent event id, if any.</param>
/// <param name="Data">The raw <c>data</c> payload as a <see cref="JsonElement"/>; null if absent.</param>
public sealed record SessionEvent(
    string Id,
    string Type,
    DateTimeOffset Timestamp,
    string? ParentId,
    JsonElement? Data);
