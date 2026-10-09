module ComposeLint.Cli.Program

open System
open System.IO
open ComposeLint.Core

type FailOn =
    | OnCritical
    | OnWarning
    | Never

type Options =
    {
        Files: string list
        Format: Report.Format
        Disabled: Set<string>
        FailOn: FailOn
        ListRules: bool
    }

let private usage =
    """composelint - lint Docker Compose files

Usage:
  composelint [options] [files...]

If no files are given, looks for compose.yaml, compose.yml,
docker-compose.yaml or docker-compose.yml in the current directory.

Options:
  -f, --format <text|json|github>        Output format (default: text)
  -d, --disable <RULE>                   Disable a rule by ID (repeatable)
      --fail-on <critical|warning|never> Lowest severity that gives exit code 1
                                         (default: warning)
      --list-rules                       List all rules and exit
  -h, --help                             Show this help

Exit codes:
  0  no findings at or above the threshold
  1  findings at or above the threshold
  2  usage error, unreadable file, or invalid YAML"""

let private defaults =
    {
        Files = []
        Format = Report.PlainText
        Disabled = Set.empty
        FailOn = OnWarning
        ListRules = false
    }

let private candidates =
    [ "compose.yaml"; "compose.yml"; "docker-compose.yaml"; "docker-compose.yml" ]

let private discoverFiles () = candidates |> List.filter File.Exists

let rec parseArgs (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] ->
        Ok
            { opts with
                Files = List.rev opts.Files
            }
    | ("-f" | "--format") :: value :: rest ->
        match Report.tryParseFormat value with
        | Some fmt -> parseArgs { opts with Format = fmt } rest
        | None -> Error $"Unknown format '{value}'. Use text, json or github."
    | ("-d" | "--disable") :: value :: rest ->
        parseArgs
            { opts with
                Disabled = opts.Disabled.Add(value.ToUpperInvariant())
            }
            rest
    | "--fail-on" :: value :: rest ->
        match value.ToLowerInvariant() with
        | "critical" -> parseArgs { opts with FailOn = OnCritical } rest
        | "warning" -> parseArgs { opts with FailOn = OnWarning } rest
        | "never" -> parseArgs { opts with FailOn = Never } rest
        | _ -> Error $"Unknown --fail-on value '{value}'. Use critical, warning or never."
    | "--list-rules" :: rest -> parseArgs { opts with ListRules = true } rest
    | [ ("-f" | "--format" | "-d" | "--disable" | "--fail-on") as flag ] -> Error $"Option '{flag}' requires a value."
    | flag :: _ when flag.StartsWith("-") -> Error $"Unknown option '{flag}'."
    | file :: rest -> parseArgs { opts with Files = file :: opts.Files } rest

let private lintFile (disabled: Set<string>) (path: string) : Result<string * Finding list, string> =
    try
        let text = File.ReadAllText path

        Linter.lint disabled text
        |> Result.map (fun findings -> path, findings)
        |> Result.mapError (fun e -> $"{path}: {e}")
    with
    | :? IOException as ex -> Error $"Cannot read '{path}': {ex.Message}"
    | :? UnauthorizedAccessException as ex -> Error $"Cannot read '{path}': {ex.Message}"

let private exitCodeFor (failOn: FailOn) (findings: Finding list) : int =
    let has severity =
        findings |> List.exists (fun f -> f.Severity = severity)

    match failOn with
    | Never -> 0
    | OnCritical -> if has Critical then 1 else 0
    | OnWarning -> if has Critical || has Warning then 1 else 0

[<EntryPoint>]
let main argv =
    if argv |> Array.exists (fun a -> a = "-h" || a = "--help") then
        printfn "%s" usage
        0
    else
        match parseArgs defaults (List.ofArray argv) with
        | Error msg ->
            eprintfn "%s" msg
            eprintfn "Run with --help for usage."
            2
        | Ok opts when opts.ListRules ->
            for r in Rules.all do
                printfn "%s  %-24s %s" r.Id r.Name r.Description

            0
        | Ok opts ->
            let files =
                if List.isEmpty opts.Files then
                    discoverFiles ()
                else
                    opts.Files

            if List.isEmpty files then
                eprintfn "No compose file found. Pass a path or run from a directory containing compose.yaml."
                2
            else
                let outcomes = files |> List.map (lintFile opts.Disabled)

                let errors =
                    outcomes
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)

                let results =
                    outcomes
                    |> List.choose (function
                        | Ok r -> Some r
                        | Error _ -> None)

                for e in errors do
                    eprintfn "%s" e

                printfn "%s" (Report.render opts.Format results)

                if not (List.isEmpty errors) then
                    2
                else
                    results |> List.collect snd |> exitCodeFor opts.FailOn
