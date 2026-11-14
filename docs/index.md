---
_layout: landing
---

# Dagline

A lightweight, in-process .NET library for running directed acyclic graphs (DAGs) of tasks in command-line apps, with live progress in the terminal.

Think Apache Airflow, but purely local: no scheduler service, no database, no web server.

> [!NOTE]
> Dagline is before 1.0. It runs real work and is tested on every change. A patch release never breaks your code, but until 1.0 the names and shapes of its public API may change in a minor release, each change listed in the [changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md). The plain-text output may change in any release and is not for parsing. The exit codes 0, 1 and 2 are stable, and every later release reads the journals an earlier one wrote: see [Compatibility](articles/compatibility.md).

## Install

```shell
dotnet add package Dagline
dotnet add package Dagline.Spectre   # optional terminal display
```

## Next steps

- [Getting started](articles/getting-started.md)
- [Concepts](articles/concepts.md)
- [Running and stopping](articles/running-and-stopping.md)
- [The terminal display](articles/terminal-ui.md)
- [Compatibility](articles/compatibility.md)
- [API reference](api/index.md)
