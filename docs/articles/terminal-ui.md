# Terminal UI

> [!NOTE]
> TODO: write this once `Dagline.Spectre` exists.

The `Dagline.Spectre` package renders a live view of a run in the terminal, built on [Spectre.Console](https://spectreconsole.net/): which tasks are queued, which are running and how far along they are, and which have finished.

The core `Dagline` package has no UI dependencies, so you can run graphs headless (in CI, for example) or build your own UI on top of its progress events.
