module ComposeLint.Core.Linter

/// Lints compose YAML text. `disabled` holds upper-case rule IDs to skip.
let lint (disabled: Set<string>) (text: string) : Result<Finding list, string> =
    Model.parse text
    |> Result.map (fun file ->
        let active = Rules.all |> List.filter (fun r -> not (disabled.Contains(r.Id)))

        [
            for svc in file.Services do
                for rule in active do
                    yield! rule.Check svc
        ]
        |> List.sortBy (fun f -> f.Line, f.RuleId))
