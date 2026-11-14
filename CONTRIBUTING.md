# Contributing to Dagline

Thanks for your interest! Bug reports, ideas, docs fixes and code are all welcome.

## Before you start

- For anything bigger than a small fix, please [open an issue](https://github.com/cquillen/dagline/issues) first so we can agree on the approach.
- By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).
- Report security issues privately. See [SECURITY.md](SECURITY.md).

## Development setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer.

```shell
dotnet build                  # build everything
dotnet test                   # run all tests (TUnit on Microsoft.Testing.Platform)
dotnet pack -c Release        # packages land in artifacts/package/release
```

Measure what a run costs, before and after a change to the engine:

```shell
dotnet run -c Release --project benchmarks/Dagline.Benchmarks
```

It runs each scenario (a large graph of nested groups, a task that reports progress very often, tasks given more warnings
than are kept, and tasks that begin and end waiting) with [BenchmarkDotNet](https://benchmarkdotnet.org/) and prints, for each,
the time taken and the bytes allocated. It takes a few minutes. Pass BenchmarkDotNet's own arguments after `--` to run fewer,
such as `-- --filter *Progress*`. Compare numbers taken on the same machine only. The measurement is not part of
`dotnet test` and is never packed.

Preview the docs site locally:

```shell
dotnet tool restore
dotnet docfx docs/docfx.json --serve   # http://localhost:8080
```

## Making changes

1. Branch from `main`. Direct pushes to `main` are blocked.
2. Keep the core `Dagline` package free of UI dependencies. Terminal UI code belongs in `Dagline.Spectre`.
3. Add or update tests. Tests use [TUnit](https://tunit.dev/) with [Shouldly](https://docs.shouldly.org/) assertions.
4. Every public type and member needs an XML doc comment. CI fails on missing ones.
5. For user-facing changes, update the docs in `docs/` and add an entry under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md).
6. Open a pull request. CI must pass on Linux, Windows and macOS. PRs are squash-merged.

Code style is defined in `.editorconfig` and enforced at build time. Design and factoring rules are in [CODING_STANDARDS.md](CODING_STANDARDS.md). All files use LF line endings.

### The public surface files

Each package records its public surface, every public and protected type and member, in two files beside its project file:
`PublicAPI.Shipped.txt`, what the last release on NuGet.org published, and `PublicAPI.Unshipped.txt`, what has changed since.
[Microsoft.CodeAnalysis.PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)
compares them with the code on every build. A public member the files do not list is `RS0016`, and one they list that the code no
longer has is `RS0017`. Both are warnings locally and errors in CI.

When you add, change or remove public API:

- Let your IDE apply the code fix for `RS0016` or `RS0017`, or update the files from the command line:

  ```shell
  dotnet format analyzers src/Dagline/Dagline.csproj --diagnostics RS0016 RS0017 --severity info
  ```

  Run it for each package you changed (`src/Dagline.Spectre/Dagline.Spectre.csproj`, `src/Dagline.Testing/Dagline.Testing.csproj`).
  The fix writes to `PublicAPI.Unshipped.txt` only. A shipped member you remove keeps its line in `PublicAPI.Shipped.txt`,
  and the fix lists it again in `PublicAPI.Unshipped.txt` with the `*REMOVED*` prefix.
- Read the diff of the files before you commit. It is the change to the public surface that the pull request makes, and a line
  you did not mean to add is a member you did not mean to make public.

At a release, a maintainer moves every line of each `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`, dropping the
lines with `*REMOVED*` and the shipped lines they name, and leaves the unshipped file holding only `#nullable enable`.

## Releasing (maintainers)

Versions come from git tags via [MinVer](https://github.com/adamralph/minver).

1. Move the `[Unreleased]` entries in `CHANGELOG.md` under a new version heading, move each package's `PublicAPI.Unshipped.txt`
   into its `PublicAPI.Shipped.txt` (see [The public surface files](#the-public-surface-files)), and merge that to `main`.
2. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`.
3. The **Release** workflow packs, publishes to NuGet.org and creates a GitHub Release.

### One-time NuGet trusted publishing setup

The release workflow authenticates to NuGet.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (GitHub OIDC), so no long-lived API key is stored.

1. Sign in to [nuget.org](https://www.nuget.org) → your username → **Trusted Publishing** → **Add**.
2. Fill in: repository owner `cquillen`, repository `dagline`, workflow file `release.yml`. Leave environment empty.
3. In the GitHub repo, add a secret **`NUGET_USER`** containing your nuget.org **username** (profile name, not email):
   `gh secret set NUGET_USER --repo cquillen/dagline`
