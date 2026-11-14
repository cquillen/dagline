# Dagline

[![CI](https://github.com/cquillen/dagline/actions/workflows/ci.yml/badge.svg)](https://github.com/cquillen/dagline/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Dagline.svg)](https://www.nuget.org/packages/Dagline)
[![Docs](https://img.shields.io/badge/docs-cquillen.github.io%2Fdagline-blue)](https://cquillen.github.io/dagline/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A lightweight, in-process .NET library for running directed acyclic graphs (DAGs) of tasks in command-line apps, with live progress in the terminal.

Think Apache Airflow, but purely local: no scheduler service, no database, no web server. You define tasks and their dependencies in C#, and Dagline runs them in dependency order inside your console app while a terminal UI shows what's queued, running and done.

> **Status:** alpha. Dagline runs real work and is tested on every change, but until 1.0 the names and shapes of its public API may change in any release, each change listed in the [changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md). The plain-text output may change in any release and is not for parsing. The exit codes 0, 1 and 2 are stable, and every later release reads the journals an earlier one wrote: see [Compatibility](https://cquillen.github.io/dagline/articles/compatibility.html).

## Packages

| Package | Description |
| --- | --- |
| [`Dagline`](https://www.nuget.org/packages/Dagline) | Core engine: define, schedule and run task graphs. No UI dependencies. |
| [`Dagline.Spectre`](https://www.nuget.org/packages/Dagline.Spectre) | Live terminal UI built on [Spectre.Console](https://spectreconsole.net/). |
| [`Dagline.Testing`](https://www.nuget.org/packages/Dagline.Testing) | For test projects only: a harness that runs one task and returns its outcome, and a clock on which every finite delay ends at once. Works with any test framework. |

```shell
dotnet add package Dagline
dotnet add package Dagline.Spectre
```

## Documentation

Guides and API reference: **https://cquillen.github.io/dagline/**

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © 2026 Craig Quillen
