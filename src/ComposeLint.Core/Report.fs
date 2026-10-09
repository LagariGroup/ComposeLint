module ComposeLint.Core.Report

open System.Text.Json

type Format =
    | PlainText
    | JsonOutput
    | GithubActions

let tryParseFormat (value: string) : Format option =
    match value.ToLowerInvariant() with
    | "text" -> Some PlainText
    | "json" -> Some JsonOutput
    | "github" -> Some GithubActions
    | _ -> None

let severityName (severity: Severity) =
    match severity with
    | Info -> "info"
    | Warning -> "warning"
    | Critical -> "critical"

let private renderText (results: (string * Finding list) list) =
    let lines =
        [
            for (file, findings) in results do
                for f in findings do
                    yield $"{file}:{f.Line}: {severityName f.Severity} {f.RuleId} [{f.ServiceName}] {f.Message}"
        ]

    let total = results |> List.sumBy (fun (_, fs) -> List.length fs)

    let summary =
        if total = 0 then
            "No issues found."
        else
            $"{total} issue(s) found."

    String.concat "\n" (lines @ [ summary ])

let private renderJson (results: (string * Finding list) list) =
    let items =
        [|
            for (file, findings) in results do
                for f in findings do
                    yield
                        {|
                            file = file
                            line = f.Line
                            rule = f.RuleId
                            severity = severityName f.Severity
                            service = f.ServiceName
                            message = f.Message
                        |}
        |]

    JsonSerializer.Serialize(items, JsonSerializerOptions(WriteIndented = true))

let private escapeData (s: string) =
    s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")

let private escapeProperty (s: string) =
    (escapeData s).Replace(":", "%3A").Replace(",", "%2C")

let private renderGithub (results: (string * Finding list) list) =
    let level (severity: Severity) =
        match severity with
        | Info -> "notice"
        | Warning -> "warning"
        | Critical -> "error"

    let lines =
        [
            for (file, findings) in results do
                for f in findings do
                    yield
                        $"::{level f.Severity} file={escapeProperty file}, line={f.Line},title={escapeProperty f.RuleId}::{escapeData f.Message}"
        ]

    String.concat "\n" lines

/// Renders results for all files. Each entry is (file path, findings).
let render (format: Format) (results: (string * Finding list) list) : string =
    match format with
    | PlainText -> renderText results
    | JsonOutput -> renderJson results
    | GithubActions -> renderGithub results
