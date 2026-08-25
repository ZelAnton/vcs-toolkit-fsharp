namespace VcsToolkit.Agent

open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module internal Redaction =
    let private credentialedUrl =
        Regex(@"(?i)\b(https?://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.CultureInvariant)

    let private namedSecret =
        Regex(
            @"(?i)\b(token|password|passwd|secret|api[_-]?key|authorization)\s*[:=]\s*[^\s,;]+",
            RegexOptions.CultureInvariant
        )

    let private bearer =
        Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.CultureInvariant)

    let redact (value: string | null) =
        match value with
        | null -> ""
        | value ->
            let withoutUserInfo = credentialedUrl.Replace(value, "$1[REDACTED]@")
            let withoutBearer = bearer.Replace(withoutUserInfo, "Bearer [REDACTED]")
            namedSecret.Replace(withoutBearer, "$1=[REDACTED]")
