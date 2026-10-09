module ComposeLint.Tests.Tests

open Xunit
open ComposeLint.Core

let private lintOk (yaml: string) : Finding list =
    match Linter.lint Set.empty yaml with
    | Ok findings -> findings
    | Error msg -> failwith msg

let private ruleIds (findings: Finding list) : string list =
    findings |> List.map (fun f -> f.RuleId) |> List.distinct |> List.sort

/// A baseline service with no findings; `extra` is appended at service level
/// (indent each extra line with 4 spaces).
let private withService (extra: string) =
    "services:\n  web:\n    image: nginx:1.27\n    healthcheck:\n      test: [\"CMD\", \"true\"]\n"
    + extra

[<Fact>]
let ``baseline service has no findings`` () = Assert.Empty(lintOk (withService ""))

// CL001 ----------------------------------------------------------------

[<Theory>]
[<InlineData("nginx")>]
[<InlineData("nginx:latest")>]
[<InlineData("registry.local:5000/app")>]
let ``CL001 flags unpinned images`` (image: string) =
    let yaml =
        $"services:\n  web:\n    image: {image}\n    healthcheck:\n      test: [\"CMD\", \"true\"]\n"

    Assert.Equal<string list>([ "CL001" ], ruleIds (lintOk yaml))

[<Theory>]
[<InlineData("nginx:1.27.2")>]
[<InlineData("nginx@sha256:0123456789abcdef")>]
[<InlineData("registry.local:5000/app:2.1")>]
[<InlineData("app:${TAG}")>]
let ``CL001 accepts pinned images`` (image: string) =
    let yaml =
        $"services:\n  web:\n    image: {image}\n    healthcheck:\n      test: [\"CMD\", \"true\"]\n"

    Assert.DoesNotContain("CL001", ruleIds (lintOk yaml))

[<Fact>]
let ``CL001 reports the line of the image key`` () =
    let finding =
        lintOk "services:\n  web:\n    image: nginx\n"
        |> List.find (fun f -> f.RuleId = "CL001")

    Assert.Equal(3, finding.Line)
    Assert.Equal("web", finding.ServiceName)

// CL002 ----------------------------------------------------------------

[<Fact>]
let ``CL002 flags a missing healthcheck`` () =
    let yaml = "services:\n  web:\n    image: nginx:1.27\n"
    Assert.Contains("CL002", ruleIds (lintOk yaml))

[<Fact>]
let ``CL002 flags a disabled healthcheck`` () =
    let yaml =
        "services:\n  web:\n    image: nginx:1.27\n    healthcheck:\n      disable: true\n"

    Assert.Contains("CL002", ruleIds (lintOk yaml))

// CL003 ----------------------------------------------------------------

[<Theory>]
[<InlineData("root")>]
[<InlineData("0")>]
[<InlineData("root:root")>]
let ``CL003 flags root users`` (user: string) =
    Assert.Contains("CL003", ruleIds (lintOk (withService $"    user: {user}\n")))

[<Fact>]
let ``CL003 flags privileged containers`` () =
    Assert.Contains("CL003", ruleIds (lintOk (withService "    privileged: true\n")))

[<Fact>]
let ``CL003 accepts an unprivileged user`` () =
    Assert.DoesNotContain("CL003", ruleIds (lintOk (withService "    user: \"1000:1000\"\n")))

// CL004 ----------------------------------------------------------------

[<Fact>]
let ``CL004 flags secrets in mapping form`` () =
    let extra = "    environment:\n      DB_PASSWORD: hunter2\n"
    Assert.Contains("CL004", ruleIds (lintOk (withService extra)))

[<Fact>]
let ``CL004 flags secrets in list form`` () =
    let extra = "    environment:\n      - API_TOKEN=abc123\n"
    Assert.Contains("CL004", ruleIds (lintOk (withService extra)))

[<Fact>]
let ``CL004 accepts interpolated values`` () =
    let extra = "    environment:\n      DB_PASSWORD: ${DB_PASSWORD}\n"
    Assert.DoesNotContain("CL004", ruleIds (lintOk (withService extra)))

[<Fact>]
let ``CL004 accepts _FILE variables`` () =
    let extra = "    environment:\n      DB_PASSWORD_FILE: /run/secrets/db_password\n"
    Assert.DoesNotContain("CL004", ruleIds (lintOk (withService extra)))

[<Fact>]
let ``CL004 ignores non-secret variables`` () =
    let extra = "    environment:\n      LOG_LEVEL: debug\n"
    Assert.Empty(lintOk (withService extra))

// CL005 ----------------------------------------------------------------

[<Theory>]
[<InlineData("8080:80")>]
[<InlineData("8080:80/tcp")>]
[<InlineData("0.0.0.0:8080:80")>]
let ``CL005 flags wildcard bindings`` (port: string) =
    let extra = $"    ports:\n      - \"{port}\"\n"
    Assert.Contains("CL005", ruleIds (lintOk (withService extra)))

[<Theory>]
[<InlineData("127.0.0.1:8080:80")>]
[<InlineData("[::1]:8080:80")>]
[<InlineData("80")>]
let ``CL005 accepts safe bindings`` (port: string) =
    let extra = $"    ports:\n      - \"{port}\"\n"
    Assert.DoesNotContain("CL005", ruleIds (lintOk (withService extra)))

[<Fact>]
let ``CL005 handles long port syntax`` () =
    let extra =
        "    ports:\n      - target: 80\n        published: 8080\n        host_ip: 127.0.0.1\n"

    Assert.DoesNotContain("CL005", ruleIds (lintOk (withService extra)))

// Linter behaviour -----------------------------------------------------

[<Fact>]
let ``disabled rules are skipped`` () =
    let yaml = "services:\n  web:\n    image: nginx\n"

    match Linter.lint (Set.ofList [ "CL001" ]) yaml with
    | Ok findings -> Assert.DoesNotContain("CL001", ruleIds findings)
    | Error msg -> failwith msg

[<Fact>]
let ``invalid YAML returns an error`` () =
    match Linter.lint Set.empty "services: [unclosed" with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``missing services section returns an error`` () =
    match Linter.lint Set.empty "version: '3'\n" with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``empty input returns an error`` () =
    match Linter.lint Set.empty "" with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error"
