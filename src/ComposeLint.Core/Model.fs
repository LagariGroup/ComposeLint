module ComposeLint.Core.Model

open System.IO
open YamlDotNet.RepresentationModel

let private line (node: YamlNode) = int node.Start.Line

let private scalar (node: YamlNode) : string option =
    match node with
    | :? YamlScalarNode as s -> Option.ofObj s.Value
    | _ -> None

let private tryGet (key: string) (map: YamlMappingNode) : YamlNode option =
    map.Children
    |> Seq.tryFind (fun kv -> scalar kv.Key = Some key)
    |> Option.map (fun kv -> kv.Value)

let private parseEnvironment (node: YamlNode) : EnvVar list =
    match node with
    | :? YamlMappingNode as m ->
        [
            for kv in m.Children do
                match scalar kv.Key with
                | Some key ->
                    yield
                        {
                            Key = key
                            Value = scalar kv.Value
                            Line = line kv.Key
                        }
                | None -> ()
        ]
    | :? YamlSequenceNode as s ->
        [
            for item in s.Children do
                match scalar item with
                | None -> ()
                | Some entry ->
                    let idx = entry.IndexOf('=')

                    if idx < 0 then
                        yield
                            {
                                Key = entry
                                Value = None
                                Line = line item
                            }
                    else
                        yield
                            {
                                Key = entry.Substring(0, idx)
                                Value = Some(entry.Substring(idx + 1))
                                Line = line item
                            }
        ]
    | _ -> []

/// Returns (host IP if given, whether a host port is published).
let private parsePortString (raw: string) : string option * bool =
    let body = raw.Split('/').[0]

    if body.StartsWith("[") then
        let close = body.IndexOf(']')

        let ip =
            if close > 1 then
                Some(body.Substring(1, close - 1))
            else
                None

        ip, true
    else
        match body.Split(':') with
        | [| _ |] -> None, false
        | [| _; _ |] -> None, true
        | [| ip; _; _ |] -> Some ip, true
        | _ -> None, true

let private parsePorts (node: YamlNode) : PortBinding list =
    match node with
    | :? YamlSequenceNode as s ->
        [
            for item in s.Children do
                match item with
                | :? YamlScalarNode as sc when not (isNull sc.Value) ->
                    let hostIp, published = parsePortString sc.Value

                    yield
                        {
                            Raw = sc.Value
                            HostIp = hostIp
                            Published = published
                            Line = line item
                        }
                | :? YamlMappingNode as m ->
                    let published = tryGet "published" m |> Option.bind scalar
                    let target = tryGet "target" m |> Option.bind scalar |> Option.defaultValue "?"
                    let hostIp = tryGet "host_ip" m |> Option.bind scalar
                    let pub = published |> Option.defaultValue ""

                    yield
                        {
                            Raw = pub + ":" + target
                            HostIp = hostIp
                            Published = published.IsSome
                            Line = line item
                        }
                | _ -> ()
        ]
    | _ -> []

let private hasHealthcheck (svc: YamlMappingNode) : bool =
    match tryGet "healthcheck" svc with
    | Some(:? YamlMappingNode as hc) ->
        tryGet "disable" hc
        |> Option.bind scalar
        |> Option.map (fun v -> v.ToLowerInvariant() <> "true")
        |> Option.defaultValue true
    | Some _ -> true
    | None -> false

let private parseService (nameNode: YamlNode) (body: YamlNode) : Service option =
    match scalar nameNode, body with
    | Some name, (:? YamlMappingNode as svc) ->
        let withLine (node: YamlNode) =
            scalar node |> Option.map (fun v -> v, line node)

        let isTrue (node: YamlNode) =
            scalar node |> Option.exists (fun v -> v.ToLowerInvariant() = "true")

        Some
            {
                Name = name
                Line = line nameNode
                Image = tryGet "image" svc |> Option.bind withLine
                User = tryGet "user" svc |> Option.bind withLine
                Privileged = tryGet "privileged" svc |> Option.filter isTrue |> Option.map line
                HasHealthcheck = hasHealthcheck svc
                Ports = tryGet "ports" svc |> Option.map parsePorts |> Option.defaultValue []
                Environment =
                    tryGet "environment" svc
                    |> Option.map parseEnvironment
                    |> Option.defaultValue []
            }
    | _ -> None

/// Parses compose YAML text into the lint model.
let parse (text: string) : Result<ComposeFile, string> =
    try
        use reader = new StringReader(text)
        let stream = YamlStream()
        stream.Load(reader)

        if stream.Documents.Count = 0 then
            Error "File is empty."
        else
            match stream.Documents.[0].RootNode with
            | :? YamlMappingNode as root ->
                match tryGet "services" root with
                | Some(:? YamlMappingNode as services) ->
                    let parsed =
                        [
                            for kv in services.Children do
                                match parseService kv.Key kv.Value with
                                | Some s -> yield s
                                | None -> ()
                        ]

                    Ok { Services = parsed }
                | _ -> Error "No 'services' mapping found."
            | _ -> Error "Top-level YAML node must be a mapping."
    with :? YamlDotNet.Core.YamlException as ex ->
        Error("Invalid YAML: " + ex.Message)
