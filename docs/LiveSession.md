# Live Revit session bridge

The **ParallelSystems > Settings > Live Session OFF/ON** ribbon button enables a
local API for the current Revit process. It starts OFF every launch and is not
persisted. Revit 2021–2026 use the same implementation. ParallelSystems must be
authorized before enabling the bridge; the toggle remains available to turn it
OFF if authorization is subsequently lost.

## Enable and connect

Build and deploy the updated add-in using the normal plugin deployment workflow,
then restart Revit to load it. An already loaded add-in cannot acquire this feature
just by rebuilding its DLL. Open a model, click **Live Session OFF**, then close
the connection-information dialog so Revit can process requests.

From PowerShell in the plugin repository:

```powershell
./tools/Invoke-RevitLiveSession.ps1 -List
$info = ./tools/Invoke-RevitLiveSession.ps1 -Command session.info
$documentId = $info.activeDocument.documentId
./tools/Invoke-RevitLiveSession.ps1 -Command elements.query -Arguments @{
    documentId = $documentId
    category = 'OST_Walls'
    limit = 20
}
```

If more than one Revit session is enabled, pass `-RevitProcessId <pid>` on every
invocation. The client refuses to choose between sessions automatically. Codex can
use this script through its terminal tools; no cloud server or MCP configuration
is required. This is a local JSON/TCP API, not an HTTP or MCP endpoint.

## Commands

All document commands require `documentId` from `session.info`, and operate on
the active document only. A document switch, close/reopen, or bridge restart
invalidates the previous context. Query session information again.

| Command | Additional arguments | Result |
| --- | --- | --- |
| `session.info` | None | Protocol, Revit version, active document and supported commands |
| `documents.list` | None | Open documents, with session-specific IDs |
| `views.list` | `offset`, `limit` | Non-template views, paginated |
| `selection.get` | None | First 200 selected elements |
| `elements.query` | Optional `category`, `nameContains`, `offset`, `limit` | Non-type elements, paginated |
| `elements.get` | `elementId` | Element identity and first 300 parameters |
| `selection.set` | `elementIds` | Select up to 200 existing elements; an empty array clears selection |
| `elements.focus` | `elementIds` | Select and show 1–200 existing elements |
| `view.activate` | `viewId` | Activate a non-template view; Revit rejects unsupported views |
| `parameter.setText` | `elementId`, `parameterName`, `expectedValue`, `value` | Update one writable text parameter in an undoable transaction |

IDs are decimal strings, preserving Revit 2024+ 64-bit IDs. `category` is a
`BuiltInCategory` enum name, such as `OST_PipeCurves`. Query limits default to 100
and range from 1–200. Use `nextOffset` to continue; `null` means the last page.
Pagination reflects the live model and is not a snapshot across requests.
Parameter double values use Revit internal units; `displayValue` is the formatted
value where Revit supplies one. Numeric parameter writes are not exposed.

For example, first read an element, then update its Comments parameter using the
exact current value, including `$null` for an unset value:

```powershell
$element = ./tools/Invoke-RevitLiveSession.ps1 -Command elements.get -Arguments @{
    documentId = $documentId; elementId = '123456'
}
$comments = @($element.parameters | Where-Object name -eq 'Comments')
if ($comments.Count -ne 1) { throw 'Comments must identify exactly one parameter.' }
./tools/Invoke-RevitLiveSession.ps1 -Command parameter.setText -Arguments @{
    documentId = $documentId; elementId = '123456'; parameterName = 'Comments'
    expectedValue = $comments[0].value; value = 'Reviewed'
}
```

Parameter names follow the document's language. Ambiguous names, read-only
parameters, stale values, unavailable documents and Revit model failures are
rejected. Model failures roll back the transaction instead of opening a remote
failure-resolution dialog. Successful edits can be undone in Revit. The bridge
does not save or synchronize the model through these commands. The optional
Dynamo commands below execute graph code with the Revit user's permissions.

## Connection and lifecycle

Each enabled process listens on an OS-assigned TCP port bound to `127.0.0.1`.
Connection details are in:

`%LOCALAPPDATA%\ParallelSystems\LiveSessions\<processId>.json`

The file contains protocol version `1`, the port and a cryptographically random
session token. It inherits the local user profile's filesystem permissions.
Treat it as a credential: do not commit, share, log, or copy it into a shared
folder. The client lists metadata without printing the token. Local processes
that can read the file can use the enabled commands, including text edits.
Each ON cycle creates a fresh token; OFF and normal shutdown close the listener,
disconnect clients, cancel pending work and delete the connection file. After a
crash a file may remain; the client filters out missing/reused process IDs and
connection errors indicate an unavailable server.

The wire format is one UTF-8 JSON object followed by LF per connection:

```json
{"token":"<from connection file>","command":"session.info","args":{}}
```

Responses are one JSON line: `{"ok":true,"result":...}` or
`{"ok":false,"error":{"code":"...","message":"..."}}`. There is no HTTP
listener, remote binding, browser CORS endpoint, or unauthenticated command.
Requests are limited to 64 KiB, JSON depth 16 and five seconds to transmit.
The server processes one client at a time, with a small socket backlog.

Revit API work executes through `ExternalEvent` on Revit's UI thread. Modal
dialogs and active commands can delay processing. Pending requests expire after
15 seconds and are removed before execution, so a timed-out pending edit cannot
run later. Once execution starts it finishes normally; it is not interrupted
mid-transaction. If a connection breaks or the client times out after execution
begins, the outcome may be unknown: read the model before retrying a write.
There are no automatic write retries.

## Validation

```powershell
dotnet run --project tests/LiveSession/LiveSession.Tests.csproj -c Release
dotnet build ParallelSystemsPlugin.2026/ParallelSystemsPlugin.2026.csproj -c Release -p:Platform=x64 -p:DeployToRevitOnBuild=false
```

The standalone tests cover transport authentication, framing, malformed requests,
queue cancellation, event rejection and shutdown without loading Revit. They do
not establish that Revit API operations succeed in a real host. Before release,
run the following checks in each supported Revit version:

1. Verify OFF on launch and the button's availability with no document open.
2. Turn ON, dismiss the dialog, discover the process and query session information.
3. List views/elements, read a selected element, set selection, focus it and switch views.
4. Edit a text parameter with its observed value, verify the result, then Undo.
5. Verify stale parameter values and stale document IDs fail without changes.
6. Keep a modal dialog open until a request expires; dismiss it and verify that
   the cancelled operation does not run.
7. Turn OFF with pending work and confirm the port closes and the file disappears.
8. Turn ON again and confirm the previous token fails. Check two Revit processes
   have separate connection files and can be selected explicitly.

## Dynamo graphs and Home.dyn

The bridge attaches to the Dynamo runtime already loaded by Revit. Open
**Manage > Dynamo** once in the active project. The plugin does not bundle a
second Dynamo engine, install Python packages, or silently replace an unsaved
workspace. Runtime APIs are checked when called; an unsupported/missing Dynamo
API returns an error rather than loading different assemblies. Compilation for
Revit 2021–2026 does not imply that a Dynamo 3.x/CPython3 graph works in each
version's bundled Dynamo. Test `Home.dyn` first in the Revit 2025/Dynamo 3.x host
for which it was authored.

| Command | Arguments | Behavior |
| --- | --- | --- |
| `dynamo.status` | None | Runtime availability, current workspace and scheduler state |
| `dynamo.inspect` | Absolute local `path` | Read JSON .dyn metadata, nodes, Python engines/code lengths, input values, connectors and SHA-256; does not execute |
| `dynamo.open` | `documentId`, `path`, `expectedSha256` | Open the inspected graph in Manual mode, without execution; reject unsaved workspaces |
| `dynamo.workspace` | `documentId` | Current workspace and node states/messages |
| `dynamo.run` | `documentId`, `viewId`, `workspaceId`, `expectedSha256`, `runId` | Evaluate the graph opened through this bridge; return diagnostics and output values |
| `dynamo.result` | `runId` | Retrieve the recorded result without executing anything |

Opening alone never runs the graph. Running requires the same document, active
view, graph workspace and file hash. Unsaved graph edits, changed files, pending
Dynamo tasks, non-Manual mode and Dynamo's execution blocks are rejected. The
bridge verifies Dynamo's DocumentManager is bound to the active Revit document.
Document IDs use Revit's document equality, so fresh API wrappers retain the
same ID while closed/reopened documents get new IDs.

`dynamo.run` temporarily uses Dynamo's synchronous scheduler inside the bridge's
ExternalEvent and restores the scheduler afterward. It forces evaluation rather
than returning Dynamo's previous cached result. Revit remains occupied during
execution; an already started graph cannot be interrupted by the toggle or a
client timeout. The bridge does not wrap Dynamo's transactions in another Revit
transaction. Graphs own their transactions and may also modify files or invoke
other applications. A graph's preview option is its own behavior, not a bridge
sandbox or a guarantee of rollback.

Generate a new GUID for each intentional run. Reusing that `runId` for the same
request returns its recorded result, including a failed/uncertain result, without
applying the graph again. Use `dynamo.result` after a connection timeout. Records
last until OFF/restart, with a maximum of 32 runs per ON cycle. Export them before
toggling OFF. Never retry an uncertain execution with a new ID until the model
has been checked.

Outputs include node states/messages and up to eight output ports from the first
200 nodes, with dictionaries/lists represented as JSON. Values are bounded to
10,000 entries, 16 nesting levels, 32,000 characters per string and approximately
500,000 text characters per run; truncation is marked. Unsupported CLR/Revit
objects are reported by type/display text, not recursively serialized.
`completed` means Dynamo evaluation finished, **not** that the Python script's
business checks passed or that changes were rolled back. Inspect its output
fields such as `status`, `error`, `warnings`, `committed` and `rolled_back`.

### First preview of Home.dyn

The supplied graph has a CPython3 node wired to a Watch, and one Boolean wired to
Python `IN[2]`. Its saved Boolean is **False**, selecting COMMIT. True selects the
script's trial/rollback path. `IN[0]` and `IN[1]` are unconnected, so the script
uses the active sheet and default configuration. It was saved in Dynamo
3.3.2.11986, in Manual mode, with no listed package dependencies.

The preview helper validates this known wiring, copies the graph with a fresh
workspace UUID, forces Manual and sets `IN[2]` to True. It never overwrites the
source or an existing output file. Its default mode is Preview; `-Mode Commit`
explicitly creates a commit copy. Recheck the script's behavior after changing
its Python implementation.

After deploying the updated plugin and restarting Revit, open a test project
and the intended original sheet. Open Dynamo, turn Live Session ON, and run:

```powershell
# From the plugin repository. Choose a new destination for each test copy.
./tools/New-HomeDynamoTestGraph.ps1 -OutputPath C:/repo/.tmp/Home.preview.dyn
$client = './tools/Invoke-RevitLiveSession.ps1'
$session = & $client -Command session.info
$doc = $session.activeDocument.documentId
$view = $session.activeDocument.activeView.elementId
& $client -Command dynamo.status
$graph = & $client -Command dynamo.inspect -Arguments @{ path = 'C:/repo/.tmp/Home.preview.dyn' }
$workspace = & $client -Command dynamo.open -Arguments @{
    documentId = $doc; path = $graph.path; expectedSha256 = $graph.sha256
}
$runId = [guid]::NewGuid().ToString()
$result = & $client -Command dynamo.run -TimeoutSeconds 600 -Arguments @{
    documentId = $doc; viewId = $view; workspaceId = $workspace.workspaceId
    expectedSha256 = $graph.sha256; runId = $runId
}
$result | ConvertTo-Json -Depth 100 | Set-Content C:/repo/.tmp/Home.preview.result.json -Encoding UTF8
# Retrieve the same record after an interrupted connection:
& $client -Command dynamo.result -Arguments @{ runId = $runId }
```

For multiple Revit sessions, add `-RevitProcessId <pid>` to every client call.
The client handles both string and DateTime discovery timestamps, preserving
UTC; this fixes PowerShell 7 filtering enabled sessions out as stale.

The supplied Python's main stages are sheet/view discovery, assembly/model
bounds, hidden Plan Detail section positioning, original section-marker
mirroring/proximity, geometry-based title alignment, scale/layout candidate
trials, collision validation and optional final application. Saved per-run
diagnostics provide a baseline for moving those stages into a native add-in.
This extension does not port that algorithm yet.

Before release, test runtime availability, unsaved workspace protection, stable
document IDs across requests, changed view/file rejection, preview output and
rollback, engine/node errors, repeat-run ID handling, and OFF/ON cleanup in Revit.
Only test a commit copy on a model intended for that change; the script's original
marker mirror can reverse on repeated application.
