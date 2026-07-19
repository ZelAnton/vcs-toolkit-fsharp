namespace VcsToolkit.Forge

open VcsToolkit.CliSupport

/// Helpers shared by the three CLI-backed adapters' pure mappers
/// (`GitHubForge`/`GitLabForge`/`GiteaForge`): normalising a raw CLI state string against
/// the unified `ForgePrState`/`ForgeIssueState` contract, and reading an "empty means
/// absent" CLI field into an `option`.
module internal Common =

    /// Whether `state` (a raw CLI state string — gh's uppercase, glab's lowercase, or
    /// tea's `String.Equals(..., OrdinalIgnoreCase)` style) case-insensitively matches
    /// `expected` (already lowercase). ASCII-only fold, matching the `Classify.asciiLower`
    /// convention (T-070) used elsewhere in this codebase for CLI-output classification —
    /// avoids the spurious matches a full-Unicode fold (`ToLowerInvariant`/`OrdinalIgnoreCase`)
    /// could introduce.
    let stateEquals (state: string) (expected: string) : bool = asciiLower state = expected

    /// Empty string (a CLI's "no value" for a required-but-blank JSON field) → `None`;
    /// anything else → `Some`.
    let strOpt (s: string) : string option = if s = "" then None else Some s
