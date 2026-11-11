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

Code style is defined in `.editorconfig` and enforced at build time. All files use LF line endings.

## Releasing (maintainers)

Versions come from git tags via [MinVer](https://github.com/adamralph/minver).

1. Move the `[Unreleased]` entries in `CHANGELOG.md` under a new version heading and merge that to `main`.
2. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`.
3. The **Release** workflow packs, publishes to NuGet.org and creates a GitHub Release.

### One-time NuGet trusted publishing setup

The release workflow authenticates to NuGet.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (GitHub OIDC), so no long-lived API key is stored.

1. Sign in to [nuget.org](https://www.nuget.org) → your username → **Trusted Publishing** → **Add**.
2. Fill in: repository owner `cquillen`, repository `dagline`, workflow file `release.yml`. Leave environment empty.
3. In the GitHub repo, add a secret **`NUGET_USER`** containing your nuget.org **username** (profile name, not email):
   `gh secret set NUGET_USER --repo cquillen/dagline`
