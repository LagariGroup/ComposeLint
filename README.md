# ComposeLint

A fast, single-binary linter for Docker Compose files, written in F#.

ComposeLint catches the mistakes that tend to slip into self-hosted stacks:
floating image tags, services with no healthcheck, containers running as root,
secrets hardcoded in `environment:` blocks, and ports accidentally exposed on
every network interface.

- **No runtime dependencies** when published as a self-contained binary
- **CI friendly:** plain text, JSON, and GitHub Actions annotation output
- **Predictable exit codes** you can gate a pipeline on
- **Easy to extend:** every rule is a small pure function

## Table of contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [Usage](#usage)
- [Rules](#rules)
- [Output formats](#output-formats)
- [Exit codes](#exit-codes)
- [CI integration](#ci-integration)
- [Project layout](#project-layout)
- [Writing a new rule](#writing-a-new-rule)
- [Development](#development)
- [Limitations](#limitations)
- [Contributing](#contributing)
- [License](#license)

## Installation

### As a .NET global tool

```bash
dotnet tool install --global ComposeLint
```

> Available once the package is published to NuGet. Until then, build from source.

### From source

```bash
git clone https://github.com/<your-user>/ComposeLint.git
cd ComposeLint
dotnet build -c Release
dotnet run --project src/ComposeLint.Cli -- docker-compose.yml
```

### As a self-contained binary

```bash
dotnet publish src/ComposeLint.Cli -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/
./publish/composelint --help
```

Swap `linux-x64` for `win-x64`, `osx-arm64`, `linux-arm64` (Raspberry Pi), etc.

## Quick start

```bash
# Lint the compose file in the current directory
composelint

# Lint specific files
composelint docker-compose.yml docker-compose.prod.yml

# Machine-readable output
composelint --format json docker-compose.yml
```

Example output:

```text
docker-compose.yml:4: warning CL001 [web] Image 'nginx:latest' is not pinned to a specific version tag or digest.
docker-compose.yml:9: warning CL005 [web] Port '8080:80' is published on all interfaces. ...
docker-compose.yml:12: critical CL004 [db] Environment variable 'POSTGRES_PASSWORD' looks like a secret with a hardcoded value. ...
3 issue(s) found.
```

## Usage

```text
composelint [options] [files...]
```

If no files are given, ComposeLint looks in the current directory for
`compose.yaml`, `compose.yml`, `docker-compose.yaml`, then `docker-compose.yml`.

| Option | Description |
| --- | --- |
| `-f`, `--format <text\|json\|github>` | Output format (default: `text`) |
| `-d`, `--disable <RULE>` | Disable a rule by ID, e.g. `-d CL002`. Repeatable. |
| `--fail-on <critical\|warning\|never>` | Lowest severity that causes exit code 1 (default: `warning`) |
| `--list-rules` | Print all rules and exit |
| `-h`, `--help` | Show help |

## Rules

| ID | Name | Severity | What it flags |
| --- | --- | --- | --- |
| CL001 | `unpinned-image` | warning | Image with no tag, or tagged `:latest`. Digests (`@sha256:...`) and interpolated tags (`${TAG}`) count as pinned. |
| CL002 | `missing-healthcheck` | info | Service without a `healthcheck` (or with `disable: true`). |
| CL003 | `root-or-privileged` | warning | `user: root` / `user: 0`, or `privileged: true`. |
| CL004 | `secret-in-environment` | critical | Variables named like `*PASSWORD*`, `*SECRET*`, `*TOKEN*`, `*API_KEY*` etc. with a literal value. |
| CL005 | `wildcard-port-binding` | warning | Published ports with no host IP (or `0.0.0.0` / `::`), which exposes them on every interface. |

### CL001: unpinned image

```yaml
# Flagged
image: nginx
image: nginx:latest

# OK
image: nginx:1.27.2
image: nginx@sha256:3f1c...
image: myapp:${APP_VERSION}
```

Floating tags make deployments non-reproducible and can pull breaking changes silently.

### CL002: missing healthcheck

Without a healthcheck, `depends_on: condition: service_healthy` cannot work and
orchestrators cannot tell a hung container from a healthy one. This is
informational because some images define their own `HEALTHCHECK`.

### CL003: root or privileged

```yaml
# Flagged
user: root
privileged: true

# OK
user: "1000:1000"
```

### CL004: secret in environment

```yaml
# Flagged
environment:
  POSTGRES_PASSWORD: hunter2
  - API_TOKEN=abc123

# OK
environment:
  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
  POSTGRES_PASSWORD_FILE: /run/secrets/db_password
```

Prefer Docker secrets, an `env_file` excluded from version control, or `${VAR}` interpolation.

### CL005: wildcard port binding

```yaml
# Flagged (reachable from the whole network)
ports:
  - "8080:80"

# OK (loopback only)
ports:
  - "127.0.0.1:8080:80"
```

Container-only ports (`- "80"`) are not flagged.

## Output formats

**text** is human-readable, one finding per line.

**json** is an array of objects, handy for scripts and dashboards:

```json
[
  {
    "file": "docker-compose.yml",
    "line": 4,
    "rule": "CL001",
    "severity": "warning",
    "service": "web",
    "message": "Image 'nginx:latest' is not pinned to a specific version tag or digest."
  }
]
```

**github** emits [workflow commands](https://docs.github.com/actions/reference/workflow-commands-for-github-actions)
so findings appear as inline annotations on pull requests.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | No findings at or above the `--fail-on` threshold |
| `1` | At least one finding at or above the threshold |
| `2` | Usage error, unreadable file, or invalid YAML |

## CI integration

### GitHub Actions

```yaml
name: Lint compose files
on: [push, pull_request]

jobs:
  composelint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet tool install --global ComposeLint
      - run: composelint --format github docker-compose.yml
```

### pre-commit (local hook)

```yaml
repos:
  - repo: local
    hooks:
      - id: composelint
        name: composelint
        entry: composelint
        language: system
        files: (docker-)?compose\.ya?ml$
```

## Project layout

```text
ComposeLint.sln
src/
  ComposeLint.Core/        # Parsing, rules, reporting (no I/O besides reading text)
    Types.fs               # Domain types: Service, Finding, Severity
    Model.fs               # YAML -> Service model (keeps line numbers)
    Rules.fs               # Rule definitions (one pure function each)
    Linter.fs              # Runs the active rules over a parsed file
    Report.fs              # text / json / github renderers
  ComposeLint.Cli/         # Argument parsing, file discovery, exit codes
    Program.fs
tests/
  ComposeLint.Tests/       # xUnit tests
```

The core library does no file or console I/O, so it is easy to test and reuse.

## Writing a new rule

1. Open `src/ComposeLint.Core/Rules.fs`.
2. Add a check function from `Service` to `Finding list`:

```fsharp
   let private checkRestartPolicy (svc: Service) =
       if svc.RestartPolicy.IsNone then
           [ make "CL006" Info svc svc.Line "No restart policy defined." ]
       else
           []
```

3. If the rule needs data the model does not expose yet, add a field to
   `Service` in `Types.fs` and populate it in `Model.fs`.
4. Register it in the `all` list at the bottom of `Rules.fs`.
5. Add tests in `tests/ComposeLint.Tests/Tests.fs`: at least one case that
   triggers the rule and one that does not.
6. Document it in the [Rules](#rules) section above.

Rule IDs are never reused, even if a rule is removed.

## Development

```bash
dotnet build
dotnet test
dotnet run --project src/ComposeLint.Cli -- --list-rules
```

## Limitations

- YAML anchors and merge keys (`<<: *default`) are not expanded.
- `extends`, `include`, and profile-based overrides are not resolved.
- Values from `.env` files are not read; `${VAR}` interpolation is treated as opaque.
- Only the `services` section is analysed (networks, volumes and secrets are ignored).

## Contributing

Issues and pull requests are welcome. Please include tests with new rules and
keep rule functions pure.

## License

MIT. Add a `LICENSE` file before publishing.