module ComposeLint.Core.Rules

open System
open System.Text.RegularExpressions

type Rule =
    {
        Id: string
        Name: string
        Description: string
        Check: Service -> Finding list
    }

let private make ruleId severity (svc: Service) line message : Finding =
    {
        RuleId = ruleId
        Severity = severity
        ServiceName = svc.Name
        Line = line
        Message = message
    }

// CL001 ------------------------------------------------------------------------------------------------

let private isPinned (image: string) =
    if image.Contains("@sha256:") then
        true
    elif image.Contains("$") then
        true // interpolated: cannot know
    else
        let lastSegment = image.Substring(image.LastIndexOf('/') + 1)

        match lastSegment.IndexOf(':') with
        | -1 -> false
        | i -> lastSegment.Substring(i + 1) <> "latest"

let private checkUnpinned (svc: Service) =
    match svc.Image with
    | Some(image, line) when not (isPinned image) ->
        [
            make
                "CL001"
                Warning
                svc
                line
                (sprintf "Image '%s' is not pinned to a specific version tag or digest." image)
        ]
    | _ -> []

// CL002 ------------------------------------------------------------------------------------------------

let private checkHealthcheck (svc: Service) =
    if svc.HasHealthcheck then
        []
    else
        [
            make "CL002" Info svc svc.Line "No healthcheck (unless the image provides one)."
        ]

// CL003 ------------------------------------------------------------------------------------------------

let private isRootUser (user: string) =
    let name = user.Split(':').[0].Trim().ToLowerInvariant()
    name = "root" || name = "0"

let private checkRoot (svc: Service) =
    [
        match svc.User with
        | Some(user, line) when isRootUser user ->
            yield
                make
                    "CL003"
                    Warning
                    svc
                    line
                    (sprintf "Service runs as root (user: %s). Use an unprivileged user." user)
        | _ -> ()

        match svc.Privileged with
        | Some line ->
            yield make "CL003" Warning svc line "Service runs in privileged mode, which gives it near-host access."
        | None -> ()
    ]

// CL004 ------------------------------------------------------------------------------------------------

let private secretKeyPattern =
    Regex(
        "PASSWORD|PASSWD|SECRET|TOKEN|API[_-]?KEY|PRIVATE[_-]?KEY|ACCESS[_-]?KEY",
        RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

let private hasLiteralValue (value: string option) =
    match value with
    | Some v -> v.Trim() <> "" && not (v.Contains("$"))
    | None -> false

let private checkSecrets (svc: Service) =
    [
        for env in svc.Environment do
            let isFileRef = env.Key.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase)

            if secretKeyPattern.IsMatch(env.Key) && not isFileRef && hasLiteralValue env.Value then
                yield
                    make
                        "CL004"
                        Critical
                        svc
                        env.Line
                        (sprintf
                            "Environment variable '%s' looks like a secret with a hardcoded value. Use Docker secrets, an env_file, or ${VAR} interpolation."
                            env.Key)
    ]

// CL005 ------------------------------------------------------------------------------------------------

let private isWildcard (ip: string option) =
    match ip with
    | None -> true
    | Some v -> v = "" || v = "0.0.0.0" || v = "::"

let private checkPorts (svc: Service) =
    [
        for p in svc.Ports do
            if p.Published && isWildcard p.HostIp then
                yield
                    make
                        "CL005"
                        Warning
                        svc
                        p.Line
                        (sprintf
                            "Port '%s' is published on all interfaces. Bind it to 127.0.0.1 (or another specific address) if it should not be reachable from the network."
                            p.Raw)
    ]

// Registry ---------------------------------------------------------------------------------------------

let private rule ruleId name description check : Rule =
    {
        Id = ruleId
        Name = name
        Description = description
        Check = check
    }

let all: Rule list =
    [
        rule "CL001" "unpinned-image" "Image has no tag or uses :latest" checkUnpinned
        rule "CL002" "missing-healthcheck" "Service has not healthcheck" checkHealthcheck
        rule "CL003" "root-or-privileged" "Service runs as root or privileged" checkRoot
        rule "CL004" "secret-in-environment" "Hardcoded secret in environment" checkSecrets
        rule "CL005" "woldcard-port-binding" "Port published on all interfaces" checkPorts
    ]
