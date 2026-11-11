# Agent instructions

Dagline: an in-process .NET 10 library for running DAGs of tasks in CLI apps, with a Spectre.Console terminal UI.

## Commands

- Build: `dotnet build`
- Test all: `dotnet test` (TUnit on Microsoft.Testing.Platform)
- Test one: `dotnet test --project tests/Dagline.Tests -- --treenode-filter "/*/*/ClassName/MethodName"`
- Pack: `dotnet pack -c Release` → `artifacts/package/release/`
- Docs preview: `dotnet tool restore && dotnet docfx docs/docfx.json --serve`

## Layout

- `src/Dagline`: core engine, NuGet package
- `src/Dagline.Spectre`: Spectre.Console UI, NuGet package
- `tests/*.Tests`: one TUnit project per package
- `samples/`: runnable console apps
- `docs/`: DocFX site, published to GitHub Pages

## Rules

- `src/Dagline` must never reference Spectre.Console or any UI or console library.
- Package versions go only in `Directory.Packages.props`. No `Version=` on a `PackageReference`.
- Tests: TUnit `[Test]` with Shouldly assertions only (not TUnit's `Assert.That`).
- Nullable on, file-scoped namespaces, LF line endings, `.editorconfig` style.
- Every public type and member needs an XML doc comment (CS1591 is an error in CI).
- Any change to public API or behaviour updates `docs/` and `CHANGELOG.md` (`[Unreleased]`) in the same PR.
- Keep the GitHub Pages docs site current: each new feature gets user-facing docs in `docs/articles/` (new page or section, added to `toc.yml`), and replace TODO stubs as the features they describe land.
- Never commit to `main`. Use a branch and PR. Versions come from git tags (MinVer); never set `<Version>`.

## Agent skills

### Issue tracker

GitHub Issues on `cquillen/dagline`, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: root `GLOSSARY.md` plus `docs/adr/`, created lazily. See `docs/agents/domain.md`.
