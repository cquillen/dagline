# Dagline.Spectre

The live terminal display for [Dagline](https://www.nuget.org/packages/Dagline), built on [Spectre.Console](https://spectreconsole.net/). While a run happens it fills the screen and redraws four times a second: how far the whole run has got, a strip of cells for each group, the tasks that are working, the tasks that are waiting on external systems, and the problems so far. When the run ends it leaves a summary on the ordinary screen. Where output is redirected to a file or a build log, the same reporter prints timestamped lines of plain text instead.

```shell
dotnet add package Dagline
dotnet add package Dagline.Spectre
```

## Example

Give the run the reporter that `SpectreReporter.Create()` returns:

```csharp
using Dagline;
using Dagline.Spectre;

using var stop = ConsoleStop.Hook();

var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Reporters = [SpectreReporter.Create()],
}, stop);

return result.Outcome.ToExitCode();
```

There is nothing to configure, and the display prints the summary itself. While it is up, your app's own logging must go to a file and not to the console.

## Learn more

- [The terminal display](https://cquillen.github.io/dagline/articles/terminal-ui.html): every pane, stopping, and your app's own output
- [Getting started](https://cquillen.github.io/dagline/articles/getting-started.html)
- [Documentation and API reference](https://cquillen.github.io/dagline/)
- [Compatibility](https://cquillen.github.io/dagline/articles/compatibility.html): the display and the plain text are for people and may change in any release
- [Changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md)
- [Source](https://github.com/cquillen/dagline)
