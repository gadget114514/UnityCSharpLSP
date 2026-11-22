---
name: unitycsharplsp
description: Use UnityCSharpLSP to get C# plus Unity prefab/scene/material intelligence without the Unity Editor. Use this skill whenever the user mentions UnityCSharpLSP, asks to find or configure a C# language server for a Unity project, wants go-to-definition/references/rename across .cs and .prefab/.unity/.asset/.mat files, wants to inspect a prefab or scene hierarchy, wants to know which prefabs use a script or field, wants to resolve GUID references in either direction (which assets reference a GUID / which GUIDs an asset references), or says that an LLM "can't find" the language server. Also covers the MCP server mode (--mcp, tools named like textDocument_definition / workspace_executeCommand), the UnityCSharpLSP.exe alias, --print-config output, and the limits of prefab searching.
---

# UnityCSharpLSP — Claude usage guide

UnityCSharpLSP is a single-file C# LSP server that also indexes Unity YAML assets
(`.prefab`, `.unity`, `.asset`, `.mat`) through a persistent asset daemon. It does not
need the Unity Editor running and does not need generated `.csproj` / `.sln` files.

Follow this guide before telling the user that something is unsupported. Most "it
can't search prefabs" or "I can't find the executable" reports come from stale
configuration, not a missing feature.

## 1. Find the executable

Search in this order:

1. `UnityCSharpLSP.exe` on `PATH` — installer-created alias (hard link) of the server.
2. `UnityCSharpLSP.Server.exe` on `PATH` — the canonical binary.
3. `%LOCALAPPDATA%\Programs\UnityCSharpLSP\` — default install directory.
4. The repo's `artifacts\publish\` — freshly published binary.

```bash
where UnityCSharpLSP.exe
where UnityCSharpLSP.Server.exe
```

Both names run the same binary. Prefer `UnityCSharpLSP.exe` in configuration and in
prompts to other LLMs; it matches the product name and is easier to discover.

## 2. Verify / generate the LSP configuration

`--print-config` prints a ready-to-use OpenCode config snippet with the resolved path:

```bash
UnityCSharpLSP.exe --print-config
```

Correct shape (the key is `extensions`, an array of file suffixes):

```json
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["<path>/UnityCSharpLSP.exe", "--stdio"],
      "extensions": [".cs", ".prefab", ".unity", ".asset", ".mat"]
    }
  }
}
```

- There is **no** `extensionToLanguage` setting in OpenCode. If someone says "only
  `.cs` is mapped", inspect the actual `opencode.json` / `opencode.jsonc` — a config
  containing only `.cs` is simply stale and prevents YAML asset buffers from ever
  reaching the server.
- `--stdio` is the default mode. stdout is reserved for LSP JSON-RPC; diagnostics go
  to stderr, so never mix human-readable output into stdout.

## 2b. MCP server mode (`--mcp`)

The same binary is also an MCP server over stdio. Use it when the agent speaks MCP but
has no LSP client (Claude Code, Cursor, etc.). Full specification: `doc/mcp-csharp-lsp.md`.

```bash
UnityCSharpLSP.exe --print-config --mcp --workspace D:/MyUnityProject
```

```json
{
  "mcpServers": {
    "unity-csharp-lsp": {
      "command": "<path>/UnityCSharpLSP.exe",
      "args": ["--mcp", "--workspace", "D:/MyUnityProject"]
    }
  }
}
```

Options: `--workspace <path>` (default: nearest ancestor of CWD with `Assets/` or
`ProjectSettings/`), `--settings <json>` (initial `workspace/didChangeConfiguration`
settings, e.g. `{"propagateRenamesToAssets":true}`), `--timeout <sec>` (per call, default 120).

### Rules for calling the tools

1. **Tool name = LSP method with `/` → `_`.** `textDocument/references` →
   `textDocument_references`, `typeHierarchy/subtypes` → `typeHierarchy_subtypes`.
2. **Arguments are the LSP params object, results are the LSP result JSON.** Nothing is
   renamed or flattened. `null` means "nothing found". LSP errors return `isError: true`
   with `{code, message}`.
3. **Positions are 0-based, UTF-16.** Editor line 12, column 9 → `{"line": 11, "character": 8}`.
   (`lsp-refs` on the CLI is 1-based; the MCP tools are not.)
4. **`textDocument.uri` may be a path.** `file:///` URI, absolute path, or a path relative
   to `--workspace` (not to the agent's CWD). Returned URIs are always `file:///`.
5. **No document sync calls.** Files are opened from disk on first use and re-sent when
   their mtime/size change. Edit with your normal file tools, then query again.
   Call `lsp_reset` after large changes (branch switch, mass generation).
   `lsp_diagnostics_cached` returns the last pushed diagnostics; use
   `textDocument_diagnostic` / `workspace_diagnostic` for fresh ones.
6. **Edit tools never write.** `textDocument_rename`, `textDocument_codeAction`,
   `codeAction_resolve`, `textDocument_formatting`, `textDocument_rangeFormatting`,
   `workspace_willRenameFiles` / `willCreateFiles` / `willDeleteFiles` return a
   `WorkspaceEdit` / `TextEdit[]`. Apply them yourself, bottom-up per file so ranges stay
   valid. A rename's edits can include `.prefab` / `.unity` / `.asset` YAML files when
   `propagateRenamesToAssets` is on.
7. **Unity commands go through `workspace_executeCommand`** with the same `arguments[0]`
   as section 4: `{"command":"unitycsharp.getSceneOrPrefabHierarchy","arguments":[{"path":"Assets/Prefabs/Enemy.prefab","maxDepth":4}]}`.
8. **Not exposed:** semantic tokens, folding/selection ranges, linked editing, inline
   values, on-type formatting, `codeLens/resolve` (display-only features).

### Typical calls

```json
{"name":"workspace_symbol","arguments":{"query":"PlayerController"}}
{"name":"textDocument_definition","arguments":{"textDocument":{"uri":"Assets/Scripts/Enemy.cs"},"position":{"line":20,"character":15}}}
{"name":"textDocument_references","arguments":{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":11,"character":8},"context":{"includeDeclaration":true},"includePatterns":["*.prefab","*.unity"]}}
{"name":"textDocument_hover","arguments":{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":5,"character":14}}}
{"name":"textDocument_documentSymbol","arguments":{"textDocument":{"uri":"Assets/Prefabs/Enemy.prefab"}}}
{"name":"textDocument_diagnostic","arguments":{"textDocument":{"uri":"Assets/Scripts/Player.cs"}}}
{"name":"textDocument_prepareCallHierarchy","arguments":{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":30,"character":17}}}
```

Hierarchy tools are two-step, as in LSP: pass an item returned by
`textDocument_prepareCallHierarchy` as `{"item": <item>}` to `callHierarchy_incomingCalls`
(same for `typeHierarchy_supertypes` / `subtypes`). To find a position when you only
know a name, call `workspace_symbol` or `textDocument_documentSymbol` first and use the
returned `selectionRange.start`.

## 3. Choose the right query for the use case

| Goal | What to use |
| :--- | :--- |
| Find where a C# class / field / method is used in prefabs, scenes, scripts | `textDocument/references` on the symbol, or `lsp-refs <file> <line> <col> --workspace <root>`; add glob filters to scope the result (see below) |
| List prefabs/scenes that use a symbol, with GameObject + line + `propertyPath` | `unitycsharp.findSymbolUsagesInAssets` (see below) |
| Read a prefab/scene as a compact GameObject/Component tree instead of raw YAML | `unitycsharp.getSceneOrPrefabHierarchy` (see below), or `textDocument/documentSymbol` on the opened asset |
| Navigate a `guid:` line inside YAML to its asset or script | `textDocument/documentLink` / `definition` / `hover` on that line |
| Which assets reference GUID X? | `unitycsharp.findGuidReferences` `{guid, maxResults?}` — on-demand YAML scan, no persistent index |
| Which GUIDs does asset A reference? | `unitycsharp.listReferencedGuids` `{path}` — includes unresolved GUIDs (`resolvedPath: null`) |
| Find assets by file name | `workspace/symbol` (matches asset names, not their contents) |
| Search arbitrary text/values inside prefab YAML | **Not an LSP feature** — use `rg` / file read on the YAML directly |

The server indexes Unity structure (GUIDs, hierarchy, serialized fields,
`m_Modifications` overrides, UnityEvent calls), not arbitrary text. Do not claim a
full-text prefab search exists. A planned `UnityCSharpLSP.Server asset-query` CLI for
AI agents exists only in `doc/prefabserver.md`; it is not implemented.

### Scoping references with glob filters

Add top-level `includePatterns` / `excludePatterns` arrays to `textDocument/references`
params. Both C# and YAML asset locations are filtered. The same fields work in
`arguments[0]` of `unitycsharp.findSymbolUsagesInAssets` and `unitycsharp.findGuidReferences`.

- Patterns without `/` match the file name: `*.cs`, `Something*.cs`, `*.asset`.
- `**` spans directories: `Assets/**/*.prefab`. A leading `!` negates a pattern.
- A leading `~` is a case-insensitive substring match on the full path: `~Weapon` matches
  `Assets/Prefabs/WeaponRifle.prefab`. Combine as `!~Generated` to exclude substrings.
- `lsp-refs` exposes the same filters as repeatable `--include` / `--exclude` options
  (comma-separated lists accepted): `lsp-refs Player.cs 12 9 --include '*.cs,*.prefab' --exclude '*Generated*'`.

```bash
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
  --command unitycsharp.findSymbolUsagesInAssets \
  --args '{"symbolName":"PlayerController","includePatterns":["*.prefab"],"excludePatterns":["*Generated*"]}'
```

## 4. Run LSP commands from the shell

`scripts/lsp_exec.py` speaks LSP over stdio and prints the result as JSON, so the
commands below are usable from Bash without an editor client:

```bash
# Structured prefab hierarchy (avoid dumping raw YAML into context)
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
  --command unitycsharp.getSceneOrPrefabHierarchy \
  --args '{"path":"Assets/Prefabs/Enemy.prefab","maxDepth":4,"includeSerializedValues":true}'

# Where a script/field is used across prefabs, scenes and assets
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
   --command unitycsharp.findSymbolUsagesInAssets \
   --args '{"symbolName":"MyGame.Player._moveSpeed","memberKind":"Field","maxResults":100}'

# Reverse GUID lookup: every asset referencing a GUID
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
  --command unitycsharp.findGuidReferences \
  --args '{"guid":"43c06344c1d574b42ab749d9a917edca","maxResults":100}'

# Forward GUID lookup: every GUID referenced by one asset (unresolved ones included)
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
  --command unitycsharp.listReferencedGuids \
  --args '{"path":"Assets/Prefabs/Player.prefab"}'

# Daemon/index state
python .agents/skills/unitycsharplsp/scripts/lsp_exec.py \
  --exe UnityCSharpLSP.exe --root D:/MyUnityProject \
  --command unitycsharp.assetServerStatus --args '{}'
```

Other commands exposed by `workspace/executeCommand` with no arguments:
`unitycsharp.reloadWorkspace`, `unitycsharp.showWorkspaceInfo`.

`--args` expects a JSON object matching the command's first `arguments` element.
`fileID`, GUID, and asset paths are returned as-is; resolve GUIDs with `documentLink`
or by reading the corresponding `.meta` file.

## 5. Build / refresh when needed

```bash
dotnet publish src/UnityCSharpLSP.Server -c Release -r win-x64 -p:PublishSingleFile=true -o ./publish
dotnet publish src/UnityCSharpLSP.Refs   -c Release -r win-x64 -p:PublishSingleFile=true -o ./publish
iscc installer.iss
```

The installer also creates the `UnityCSharpLSP.exe` alias and adds the install
directory to the User `PATH` when that task is selected.

## Caveats to pass on to the user

- Cross-asset rename propagation and most YAML diagnostics are opt-in settings:
  `propagateRenamesToAssets`, `reportDanglingAssetReferences`,
  `reportPrefabOverrideImpact`, `reportUnattachedScripts` (all default `false`).
- UnityCSharpLSP writes YAML directly and does not coordinate with the Unity Editor's
  serializer. Prefer Unity-driven renames while the Editor is open.
- Only one asset daemon runs per project root; do not run multiple LSP instances
  against the same root.
