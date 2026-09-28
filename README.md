# svc-truth

Read-only truth about local background services for agents and humans: crash loops, restart counts, last exit codes, and when a service's own health check disagrees.

A service can crash-loop for hours while its own `doctor` command still reports everything fine. The only honest source is the supervisor: on macOS that is launchd. `svc-truth` reads what launchd actually observed — state, run count, last exit status, log tails — turns that into a verdict, and can run the service's own health command to flag a **contradiction** when that command says "healthy" while launchd shows a crash loop.

```
$ svc-truth io.example.bridge --doctor "io-example-bridge doctor" --json
```

It never starts, stops, reloads or edits anything. First version: macOS only (launchd gui domain). Linux systemd support is planned for a later version.

## What it does

- Lists the current user's loaded launchd jobs from the gui domain (`launchctl list`), narrowed by a label prefix or shown as one detailed job for an exact label.
- Per job, reads `launchctl print gui/<uid>/<label>` and reports: label, state, pid, run count, last exit status (with the signal name when the job died by signal), the stdout and stderr log paths from the job's plist, and the tail of the stderr log (the stdout tail when stderr is empty), trimmed to a few lines.
- Computes a verdict per job (see [verdict rules](#verdict-rules)): `healthy`, `crash-looping`, `recovered`, `loaded-never-ran`, `exited-failed` or `unknown`.
- Optionally runs a doctor command (`--doctor "<command>"`, through `sh`, 10 s timeout) and reports a contradiction when it exits 0 while the verdict is `crash-looping` or `exited-failed`.
- Reads the process uptime of suspicious running jobs (`ps -o etime= -p <pid>`, read-only) so a job that has recovered from a crash loop is not reported as crash-looping.
- Prints short human-readable lines by default, or one stable JSON object with `--json` (see [JSON output](#json-output)).

## What it does not do

- It is strictly read-only. It never runs launchctl subcommands that change state (`load`, `unload`, `bootstrap`, `bootout`, `kickstart`, `kill`, `enable`, `disable`, ...). The command runner refuses them by construction, and tests assert that only `launchctl list` and `launchctl print` are ever invoked.
- It never writes to a job's files: plists and logs are only read.
- It does not watch continuously; one run is one report. A watch mode may come later.

## Requirements

- macOS on Apple silicon (arm64)
- .NET 10 SDK — only to build; the installed binary is self-contained native AOT and needs no runtime

## Install

```sh
git clone https://github.com/lbildzinkas/svc-truth.git
cd svc-truth
./install.sh
```

`install.sh` builds a native ahead-of-time single-file binary into `~/.local/bin/svc-truth` (override with `SVC_TRUTH_INSTALL_DIR`). It is safe to re-run: each run rebuilds and replaces the installed copy.

## Examples

List every loaded job, one short line each:

```sh
svc-truth
```

Narrow to a label prefix:

```sh
svc-truth io.github.example
```

One job in detail (exact label): state, run count, last exit status, log paths, log tail, verdict:

```sh
svc-truth io.github.example.bridge
```

The agent-shaped call: one job, its own doctor command, machine-readable output:

```sh
svc-truth io.github.example.bridge --doctor "bridge doctor" --json
```

An agent reads `jobs[].verdict`, `jobs[].doctor.contradiction` and the `exitCode`, and knows — without trusting the service's self-report — whether the supervisor observes a crash loop.

## Verdict rules

Evaluated per job, in this order:

1. **unknown** — the job's `launchctl print` output could not be read or parsed.
2. **loaded-never-ran** — run count is 0: the job is loaded but has never run.
3. **crash-looping** — the last exit status is non-zero and either
   - the run count rose by **3 or more** across two samples taken **3 seconds** apart (restart rate and average restart interval are then reported), or
   - the run count is **10 or more** outright (a long-lived loop; no waiting needed) and the job has not recovered (next rule).
4. **recovered** — the crash-loop threshold would fire (run count 10+ with a non-zero last exit) but the job is running and its process has been up **60 seconds or more** (read with `ps -o etime=`): launchd throttles restarts to roughly one every 10 seconds, so a process that outlived six throttle windows without dying has broken the loop. Reported as recovered with the past restart count. Jobs that are not running, or whose process started less than 60 seconds ago, still get `crash-looping`; a run count observed rising during sampling is always `crash-looping` (a stable process cannot produce a fresh rise, so that reading wins).
5. **exited-failed** — the last exit status is non-zero (exit code, or killed by signal), the job is not running now, and neither crash-loop condition held.
6. **healthy** — running (including a running job whose exit status is unavailable), or last exit was clean (status 0), or running now after an earlier failed run that is not looping.

Some system-provided jobs appear in `launchctl list` but cannot be read with `launchctl print` (launchd answers "Could not find service"). For those, the verdict falls back to the list row alone: running → `healthy`, clean last exit → `healthy`, failed last exit → `exited-failed` (run count and log paths stay unknown, and the reason says so). Jetsammed jobs are rescued the same way: their print output parses but carries only a `last exit reason` line, never a last exit code, so the list row's signal status is used instead (a jetsammed job reports `exited-failed` with `lastExitSignal`). Only when neither source yields a usable exit status does the verdict become `unknown`.

Sampling happens only for jobs whose first sample looks suspicious (non-zero last exit status), so a plain listing stays fast: the one shared 3-second delay is paid only when something is actually failing. The process-uptime check (`ps -o etime=`) also runs only for suspicious jobs that are currently running.

## Exit codes

| Code | Meaning                                                                                     |
|------|---------------------------------------------------------------------------------------------|
| 0    | no selected job is failing: healthy, recovered, or loaded-never-ran (no contradiction)        |
| 2    | at least one selected job is `crash-looping`, `exited-failed`, or contradicted by its doctor |
| 3    | usage error or read error (unknown verdict, unreadable gui domain, no matching label)        |

## JSON output

`--json` prints exactly one object. Fields are camelCase and `schemaVersion` (currently `1`) marks the shape; fields will only be added, never silently renamed, within a schema version. **Every documented field is always emitted** — an empty value serializes as `null` (or `false` for booleans) instead of being omitted, so consumers can read one stable shape.

Top level:

| Field          | Type   | Meaning                                          |
|----------------|--------|--------------------------------------------------|
| schemaVersion  | number | currently `1`                                    |
| generatedAtUtc | string | report time, `yyyy-MM-ddTHH:mm:ssZ`              |
| domain         | string | launchd domain read, e.g. `gui/501`              |
| selection      | string | the label argument, `null` when listing all      |
| jobs           | array  | one entry per selected job (below)               |
| summary        | object | counts per verdict plus `total`, `contradictions` |
| exitCode       | number | the exit code this run will exit with            |

Per job (`jobs[]`):

| Field                    | Type     | Meaning                                                                       |
|--------------------------|----------|-------------------------------------------------------------------------------|
| label                    | string   | launchd label                                                                 |
| state                    | string   | launchd state (`running`, `not running`), `null` when unreadable              |
| pid                      | number   | current pid, `null` when not running                                          |
| runCount                 | number   | launchd run count, `null` when `launchctl print` is unreadable              |
| lastExitCode             | number   | last exit status; negative = killed by that signal; `null` when never exited  |
| lastExitSignal           | string   | signal name (`SIGTERM`, ...) when killed by signal, else `null`               |
| logPaths                 | object   | `stdout`/`stderr` paths declared in the job's plist, `null` when unknown       |
| stderrTail               | array    | last lines of the stderr log, trimmed, `null` when unavailable                |
| stdoutTail               | array    | last lines of the stdout log, non-`null` only when the stderr log is empty    |
| restartRatePerMinute     | number   | restarts per minute, `null` unless measured across the two samples            |
| restartIntervalSeconds   | number   | average seconds between restarts, `null` unless measured                      |
| verdict                  | string   | `healthy` \| `crash-looping` \| `recovered` \| `loaded-never-ran` \| `exited-failed` \| `unknown` |
| reason                   | string   | short explanation of the verdict                                               |
| doctor                   | object   | `null` without `--doctor`; otherwise `command`, `exitCode`, `timedOut`, `stdoutTail`, `stderrTail`, `contradiction` |

Example:

```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-09-28T12:00:00Z",
  "domain": "gui/501",
  "selection": "io.github.example.bridge",
  "jobs": [
    {
      "label": "io.github.example.bridge",
      "state": "not running",
      "pid": null,
      "runCount": 5400,
      "lastExitCode": 78,
      "lastExitSignal": null,
      "logPaths": {
        "stdout": "/Users/example/Library/Logs/bridge/launchd.out.log",
        "stderr": "/Users/example/Library/Logs/bridge/launchd.err.log"
      },
      "stderrTail": ["2026-09-28T11:59:50Z bridge: fatal: connection refused (127.0.0.1:8081)"],
      "stdoutTail": null,
      "restartRatePerMinute": 6.0,
      "restartIntervalSeconds": 10.0,
      "verdict": "crash-looping",
      "reason": "run count rose by 3 in 3.0s with exit code 78",
      "doctor": {
        "command": "bridge doctor",
        "exitCode": 0,
        "timedOut": false,
        "stdoutTail": ["all checks passed"],
        "stderrTail": null,
        "contradiction": true
      }
    }
  ],
  "summary": { "total": 1, "healthy": 0, "crashLooping": 1, "recovered": 0, "loadedNeverRan": 0, "exitedFailed": 0, "unknown": 0, "contradictions": 1 },
  "exitCode": 2
}
```

## How it reads data

- `launchctl list` for the job inventory (label, pid, last exit status).
- `launchctl print gui/<uid>/<label>` per job (state, run count, last exit status, plist path).
- `ps -o etime= -p <pid>` for the process uptime of suspicious running jobs (the recovered verdict).
- The job's plist for `StandardOutPath` / `StandardErrorPath` (XML plists; binary plists leave the paths unknown).
- The log files themselves, read from the end, for the tails.

All process spawning goes through one command runner that refuses state-changing launchctl subcommands.

## Development

```sh
dotnet test          # unit tests with captured launchctl output
dotnet test -e SVC_TRUTH_LIVE_TESTS=1   # plus read-only tests against the real gui domain
./install.sh         # build and install the native binary
```

## License

MIT — see [LICENSE](LICENSE).
