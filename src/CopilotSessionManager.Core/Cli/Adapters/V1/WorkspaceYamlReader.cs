using System.Globalization;
using CopilotSessionManager.Core.Models;
using YamlDotNet.RepresentationModel;

namespace CopilotSessionManager.Core.Cli.Adapters.V1;

/// <summary>
/// Reads <c>workspace.yaml</c>. The format is a flat mapping; we tolerate
/// missing fields and unknown extras.
/// </summary>
internal sealed class WorkspaceYamlReader
{
    public WorkspaceManifest Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        using var sr = new StringReader(yaml);
        stream.Load(sr);

        if (stream.Documents.Count == 0 ||
            stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("workspace.yaml does not contain a top-level mapping.");
        }

        var id = GetString(root, "id") ?? string.Empty;

        return new WorkspaceManifest(
            Id: id,
            Cwd: GetString(root, "cwd"),
            GitRoot: GetString(root, "git_root"),
            Repository: GetString(root, "repository"),
            HostType: GetString(root, "host_type"),
            Branch: GetString(root, "branch"),
            SummaryCount: GetInt(root, "summary_count") ?? 0,
            CreatedAt: GetTimestamp(root, "created_at"),
            UpdatedAt: GetTimestamp(root, "updated_at"),
            Summary: GetString(root, "summary"));
    }

    private static string? GetString(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static int? GetInt(YamlMappingNode root, string key)
    {
        var s = GetString(root, key);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static DateTimeOffset? GetTimestamp(YamlMappingNode root, string key)
    {
        var s = GetString(root, key);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            s,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var ts)
            ? ts
            : null;
    }
}
