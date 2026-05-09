namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Coarse cost / capability tier used to color-code model badges.
/// </summary>
public enum ModelTier
{
    /// <summary>Model is not in the embedded catalog and we have no cost rates.</summary>
    Unknown = 0,

    /// <summary>Cheap, fast models (e.g., Haiku 4.x, GPT-5 mini).</summary>
    Fast = 1,

    /// <summary>Default-grade models (e.g., Sonnet 4.x, GPT-5.4).</summary>
    Standard = 2,

    /// <summary>Top-tier reasoning models (e.g., Opus 4.x, GPT-5.5).</summary>
    Premium = 3,
}
