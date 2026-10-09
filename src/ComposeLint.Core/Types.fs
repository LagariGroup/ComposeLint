namespace ComposeLint.Core

type Severity =
    | Info
    | Warning
    | Critical

type Finding =
    {
        RuleId: string
        Severity: Severity
        ServiceName: string
        Line: int
        Message: string
    }

type PortBinding =
    {
        Raw: string
        HostIp: string option
        Published: bool
        Line: int
    }

type EnvVar =
    {
        Key: string
        Value: string option
        Line: int
    }

type Service =
    {
        Name: string
        Line: int
        Image: (string * int) option
        User: (string * int) option
        Privileged: int option
        HasHealthcheck: bool
        Ports: PortBinding list
        Environment: EnvVar list
    }

type ComposeFile = { Services: Service list }
