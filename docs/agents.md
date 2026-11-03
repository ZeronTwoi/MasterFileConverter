# Using Master File Converter from an AI agent or a script

The app can be driven without its window, in two ways that do the same thing: an **MCP server**
for agents that speak the Model Context Protocol, and a **command line with JSON output** for
anything that can run a program. It all works on local files, offline (only `setup_engines`
downloads anything). A source file is never changed or replaced.

## Connect an agent (MCP)

In the window, menu (F9) → **Copy AI agent setup**, then paste into your agent's MCP settings. Or
print the same thing from a terminal:

```sh
masterfileconverter --agent-setup            # Linux
MasterFileConverter-cli --agent-setup        # Windows
```

```json
{
  "mcpServers": {
    "masterfileconverter": {
      "command": "C:\\Users\\you\\AppData\\Local\\Programs\\Master File Converter\\MasterFileConverter-cli.exe",
      "args": ["--mcp"]
    }
  }
}
```

The client starts the server by itself and it ends with the client. On Windows it must be the
`-cli` program: the window's program does not keep a pipe open.

| Tool | What it does |
|---|---|
| `list_formats` | what can be read and what each format can become (`source` for one format) |
| `list_settings` | the settings of a conversion: key, kind, range or choices, default, what it does |
| `convert` | converts; returns each result's path, size, status and any error |
| `status` | version, platform, and which engines this computer has |
| `setup_engines` | downloads FFmpeg and 7-Zip (100–150 MB) — needed once for audio, video and .7z |

`convert` takes `inputs`, `target`, and optionally `out_dir`, `result`, `size_percent`, `speed`,
`settings`, `when_exists`, `each`, `use_gpu` and `dry_run`. Progress is sent if the client asks
for it (`_meta.progressToken`), and `notifications/cancelled` stops a conversion.

## Modifying a file

Convert it **to its own format** with settings. The result is a new file beside the source:

```json
{ "inputs": ["/photos/big.jpg"], "target": "jpg", "settings": { "maxWidth": 1920, "metadata": "strip" } }
{ "inputs": ["/video/talk.mp4"], "target": "mp4", "result": "small" }
```

Ask `list_settings` which settings a conversion has rather than guessing. Compressing with only
`result` (or `size_percent`) keeps the source's bytes when the file cannot get smaller, and says
so in the result's `note`.

## How far a file is taken: `result`

The same five positions as the window's Result slider:

| `result` | Size aimed for | Speed |
|---|---|---|
| `smallest` | about 25 % of the source | fast |
| `small` | about 50 % | fast |
| `good` | about 75 % | balanced |
| `full` (default) | full quality, nothing thrown away that the format can keep | balanced |
| `best` | full quality, the smallest file at that quality | slowest |

`size_percent` (10–100) and `speed` (`fastest`, `fast`, `balanced`, `best`) override its two
halves; a precise setting such as `quality` or `targetMb` overrides the size.

## The command line

```sh
masterfileconverter photo.png webp --json
masterfileconverter a.jpg b.jpg c.jpg pdf --out /tmp/out --result good --json
masterfileconverter clip.mov mp4 --set maxWidth=1280 --dry-run --json

masterfileconverter --list-formats [<source-ext>] --json
masterfileconverter --list-settings mov mp4 --json
masterfileconverter --status --json
```

With `--json`, stdout holds exactly one JSON document, also when something goes wrong. (The
progress of a single file goes to stderr.) The window's program never waits on Windows, so scripts
use `MasterFileConverter-cli`.

| Exit code | Meaning |
|---|---|
| 0 | everything converted (or skipped as asked) |
| 1 | at least one file failed; the others are done — see `results` |
| 2 | the request is wrong; nothing was done — see `error` |
| 3 | an engine it needs is not installed; run `--setup-tools` |

## What comes back

A result of `convert`, or of `--json`:

```json
{
  "ok": true,
  "dryRun": false,
  "version": "2.8.0",
  "target": "webp",
  "combined": false,
  "results": [
    {
      "status": "converted",
      "inputs": ["/photos/a.png"],
      "inputBytes": 182311,
      "output": "/photos/a.webp",
      "outputIsFolder": false,
      "outputBytes": 40120,
      "seconds": 0.21,
      "note": "lossless"
    }
  ]
}
```

- `status`: `converted`, `skipped`, `failed`, `cancelled`, or in a dry run `planned`.
- `combined`: several inputs became one file (pictures → pdf/gif/tiff, anything → an archive). This
  is what happens unless `each` is set; `inputs` then lists them in the order they went in.
- `outputIsFolder`: the result is a folder (a PDF's pages, a video's frames); `outputFiles` lists
  what is in it. In a dry run, `mayBecomeFolder` warns of this, since a PDF's page count is only
  known on reading it. `replaces` says an existing file would be replaced.
- A failed file has `error: { code, message }`. A request that cannot run at all has `ok: false`
  and a top-level `error`.

| `error.code` | What to do |
|---|---|
| `bad_arguments` | fix the request |
| `input_not_found` | fix the path |
| `unsupported_conversion` | choose from `error.validTargets` |
| `invalid_setting` | see `list_settings` |
| `engine_missing` | run `setup_engines` / `--setup-tools` |
| `conversion_failed` | the file itself could not be converted; see the message |
| `cancelled` | it was cancelled |

Field names and error codes are kept across versions: new ones may be added, these do not change.
