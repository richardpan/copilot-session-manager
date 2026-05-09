namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Intent label users can assign to a session. Each session has exactly one
/// type; <see cref="Exploratory"/> is the default for sessions the user has
/// not yet labeled.
/// </summary>
public enum SessionType
{
    /// <summary>Default — open-ended poking around.</summary>
    Exploratory = 0,

    /// <summary>Focused investigation of a topic or library.</summary>
    Research = 1,

    /// <summary>Building a new feature.</summary>
    Feature = 2,

    /// <summary>Diagnosing or fixing a bug.</summary>
    Bug = 3,

    /// <summary>Code or structural cleanup with no behavior change.</summary>
    Refactor = 4,

    /// <summary>Documentation work.</summary>
    Docs = 5,

    /// <summary>Build, CI, dependency, or environment work.</summary>
    Infra = 6,

    /// <summary>Throwaway prototype or spike.</summary>
    Experiment = 7,
}
