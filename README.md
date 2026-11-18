# UnityCSharpLSP

[![Release](https://img.shields.io/github/v/release/gadget114514/UnityCSharpLSP)](https://github.com/gadget114514/UnityCSharpLSP/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A lightweight, high-performance C# Language Server Protocol (LSP) server tailored specifically for **Unity** projects.

Powered by .NET 8 and Microsoft Roslyn, **UnityCSharpLSP** provides full C# code intelligence (diagnostics, hover, go-to-definition, code completion, and find references) completely autonomously—**without requiring the Unity Editor to be running and without relying on generated `.csproj` / `.sln` files**.

---

## ✨ Key Features

- **🚀 Zero Unity Editor Dependency**:
  - Automatically locates the matching Unity Editor and managed DLLs (`UnityEngine.dll`, etc.) via Unity Hub configurations (`editors.json`) and standard install paths.
  - Automatically parses `Assets/**/*.asmdef`, GUID references, and `Packages/manifest.json`.
  - Works out of the box right after `git clone`—no need to open the Unity Editor to regenerate project files!
- **🔍 Unity-Aware Diagnostics & Suppressors**:
  - Real-time Roslyn compiler error/warning diagnostics.
  - Suppresses false-positive `CS0649` ("Field is never assigned to...") warnings on `[SerializeField]` private fields.
- **⚡ Core Language Server Features**:
  - **Go to Definition (`textDocument/definition`)**: Seamless navigation across `.asmdef` boundaries via Roslyn project references.
  - **Hover Tooltips (`textDocument/hover`)**: Rich method and type signatures with XML `<summary>` doc comments.
  - **Code Completion (`textDocument/completion`)**: Dot member access completions, local/type completions, and **MonoBehaviour Magic Methods snippet completions** (`Update`, `Start`, `Awake`, `OnCollisionEnter`, etc.).
  - **Find References (`textDocument/references`)**: Solution-wide symbol search across all assemblies.
  - **Go to Implementation (`textDocument/implementation`)**: Jump from interfaces and abstract/virtual members to their implementations.
  - **Document Symbols (`textDocument/documentSymbol`)**: Hierarchical outline of namespaces, types, and members for the current file.
   - **Workspace Symbols (`workspace/symbol`, `workspaceSymbol/resolve`)**: Project-wide symbol search by name with lazily resolved symbol details.
   - **Call Hierarchy (`textDocument/prepareCallHierarchy`, `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls`)**: Explore callers and callees using Roslyn semantic analysis.
   - **Will-Save Hooks (`textDocument/willSave`, `textDocument/willSaveWaitUntil`)**: Standard save lifecycle support.
 - **🗂 Multi-Root Workspaces**:
   - Accepts multiple `workspaceFolders`; a `WorkspaceRouter` builds one Roslyn workspace per root and routes each document to the longest matching root prefix. Single-root (`rootUri` / `rootPath`) behaviour is unchanged.
 - **🧩 Workspace Commands & Configuration**:
   - `workspace/executeCommand` supports `unitycsharp.reloadWorkspace` (rebuild from project roots) and `unitycsharp.showWorkspaceInfo` (projects/assemblies/documents summary).
   - `workspace/didChangeConfiguration` stores typed settings (`enableDiagnostics`, `predefinedSymbols`) and can disable diagnostic publication.
   - Full file-operation events (`workspace/willCreateFiles` / `didCreateFiles`, `willRenameFiles` / `didRenameFiles`, `willDeleteFiles` / `didDeleteFiles`) keep the workspace in sync with editor file changes.
   - Advertises `positionEncoding: utf-16` and work-done progress for long-running commands.
 - **👀 Dynamic File Monitoring**:
   - Built-in file watcher and `workspace/didChangeWatchedFiles` handler automatically detect newly created, modified, or deleted `.cs` files and update the Roslyn compilation in real-time.
- **📦 Single-File Binary**:
  - Bundled with all Roslyn and JSON-RPC dependencies into a single, standalone executable.

---

## ⚖️ Comparison & Trade-offs (Pros & Cons)

How does **UnityCSharpLSP** compare to existing C# language servers (**OmniSharp**, **csharp-ls**, and **VS Code's built-in Roslyn LSP / C# Dev Kit**) when working with Unity projects?

### Comparison Matrix

| Feature / Capability | UnityCSharpLSP (This Project) | OmniSharp (`omnisharp-roslyn`) | `csharp-ls` | VS Code Built-in (Roslyn LSP / C# Dev Kit) |
| :--- | :--- | :--- | :--- | :--- |
| **Unity Editor Running Required?** | ❌ **No** (Fully autonomous) | ⚠️ **Yes** (Needs Unity to export `.csproj`) | ⚠️ **Yes** (Needs Unity to export `.csproj`) | ⚠️ **Yes** (Needs Unity to export `.csproj`) |
| **`.csproj` / `.sln` Files Required?** | ❌ **No** (Direct in-memory Roslyn AST) | ⚠️ **Yes** (Fails if missing/out-of-sync) | ⚠️ **Yes** (Fails if missing/out-of-sync) | ⚠️ **Yes** (Fails if missing/out-of-sync) |
| **Native `.asmdef` & GUID Resolution** | ✅ **Built-in** (First-class citizen) | ❌ No (Relies on MSBuild projection) | ❌ No (Relies on MSBuild projection) | ❌ No (Relies on MSBuild projection) |
| **Dynamic File Creation Recognition** | ✅ **Instant** (In-memory file monitor) | ⚠️ Delayed (Must focus Unity Editor) | ⚠️ Delayed (Must focus Unity Editor) | ⚠️ Delayed (Must focus Unity Editor) |
| **Unity Symbol Auto-Calculation** | ✅ **Automatic** (from `ProjectVersion.txt`) | ⚠️ Brittle (Relies on Unity's export) | ⚠️ Brittle (Relies on Unity's export) | ⚠️ Brittle (Relies on Unity's export) |
| **`[SerializeField]` CS0649 Suppressor** | ✅ **Built-in** (No false warnings) | ❌ No (Manual `#pragma` required) | ❌ No (Manual `#pragma` required) | ⚠️ Partial (Requires Unity extension) |
| **MonoBehaviour Message Completions** | ✅ **Built-in** (Snippets for `Update` etc.) | ❌ No | ❌ No | ⚠️ Requires Unity extension |
| **Headless / CI / AI Agent Ready** | ✅ **Zero-friction** (`--stdio`, `--print-config`) | ❌ Heavy / Requires MSBuild setup | ⚠️ Medium | ❌ Heavy / Requires VS Code host |
| **General Non-Unity C# Projects** | ❌ **No** (Unity-exclusive) | ✅ **Yes** (Broad .NET ecosystem) | ✅ **Yes** (Broad .NET ecosystem) | ✅ **Yes** (Broad .NET ecosystem) |
| **Licensing** | ✅ **MIT** (Permissive Open Source) | ✅ **MIT** (Permissive Open Source) | ✅ **MIT** (Permissive Open Source) | ⚠️ Proprietary (Microsoft Visual Studio EULA) |

---

### In-Depth Breakdown

#### 1. UnityCSharpLSP vs. OmniSharp
- **Pros of UnityCSharpLSP**:
  - **Works out of the box right after `git clone`**: OmniSharp breaks unless the Unity Editor has generated `.csproj`/`.sln` files. UnityCSharpLSP directly reconstructs the workspace from `ProjectSettings/`, `Packages/`, and `.asmdef` files.
  - **Zero file lock conflicts**: When Unity compiles in the background, it frequently locks `Library/ScriptAssemblies/*.dll`, causing OmniSharp read exceptions. UnityCSharpLSP reads assembly bytes in memory to avoid file locking deadlocks.
  - **No Unity focus switching**: Creating a new `.cs` file is recognized instantly by the internal monitor without having to switch focus back and forth between your code editor and the Unity Editor.
- **Cons of UnityCSharpLSP**:
  - OmniSharp supports any standard C# project (ASP.NET Core, WPF, MAUI, etc.), whereas UnityCSharpLSP is exclusively built for Unity projects.

#### 2. UnityCSharpLSP vs. `csharp-ls`
- **Pros of UnityCSharpLSP**:
  - `csharp-ls` relies strictly on MSBuild project evaluation. In Unity projects, package dependencies, GUID-based assembly references, and `versionDefines` frequently confuse `csharp-ls`, causing broken type references. UnityCSharpLSP natively parses Unity's package and assembly definition schema.
  - UnityCSharpLSP provides Unity-tailored semantic enhancements (suppressing `CS0649` on `[SerializeField]`, auto-suggesting `MonoBehaviour` lifecycle methods).
- **Cons of UnityCSharpLSP**:
  - `csharp-ls` is a great lightweight general-purpose C# server for standard .NET solutions.

#### 3. UnityCSharpLSP vs. VS Code Built-in Roslyn LSP (C# Dev Kit)
- **Pros of UnityCSharpLSP**:
  - **Truly Open & Cross-Platform**: Microsoft's C# Dev Kit is governed by a proprietary license (restricted in commercial/enterprise environments) and tightly coupled to VS Code. UnityCSharpLSP is MIT-licensed and connects to any editor (Neovim, Zed, Helix, VS Code) or AI agent (OpenCode, Claude Code).
  - **Autonomous & Fast**: No multi-process service hub overhead; operates as a single standalone native executable.
- **Cons of UnityCSharpLSP**:
  - Microsoft C# Dev Kit offers full solution explorer UI, complex Roslyn refactoring code actions, and deep integration with the Official Unity Extension when inside VS Code.

---

## 📋 Prerequisites

Before running or building **UnityCSharpLSP**, ensure the .NET 8.0 SDK is installed on your machine.
On Windows, you can install it with a single command via `winget`:

```bash
winget install Microsoft.DotNet.SDK.8
```

---

## 📥 Installation

### Method 1: Windows Installer (Recommended)

1. Go to the [Releases](https://github.com/gadget114514/UnityCSharpLSP/releases) page.
2. Download and run `UnityCSharpLSP-v*-Setup.exe`.
3. Follow the wizard. It will install the server and **automatically add the installation directory to your User `PATH`**.
4. Open a new terminal, and `UnityCSharpLSP.Server.exe` will be directly accessible from anywhere.

### Method 2: Portable Zip Archive

1. Download `UnityCSharpLSP-v*-win-x64.zip` from [Releases](https://github.com/gadget114514/UnityCSharpLSP/releases).
2. Extract the archive and place `UnityCSharpLSP.Server.exe` into any directory on your system `PATH`.

### Method 3: Build from Source

Ensure [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is installed.

```bash
# Clone the repository
git clone https://github.com/gadget114514/UnityCSharpLSP.git
cd UnityCSharpLSP

# Publish as a standalone single-file binary
dotnet publish src/UnityCSharpLSP.Server -c Release -r win-x64 -p:PublishSingleFile=true -o ./publish
```

The compiled single-file binary will be located at `./publish/UnityCSharpLSP.Server.exe`.

---

## ⚡ Quick Setup

The shortest path from zero to a working setup—let your LLM (AI agent) do the wiring:

1. **Install** UnityCSharpLSP using the Windows Installer above. The installer adds the install directory to your User `PATH`.
2. **Ask your LLM to find it on `PATH`**, for example:
   > Search `PATH` for `UnityCSharpLSP.Server.exe` and configure UnityCSharpLSP as my C# language server.
3. The LLM locates the executable (or runs `UnityCSharpLSP.Server.exe --print-config`) and writes the appropriate LSP configuration for your editor/agent, so `unitycsharplsp` is ready to use.

> On Windows you can verify the binary is on `PATH` with `where UnityCSharpLSP.Server.exe` (or `which UnityCSharpLSP.Server.exe` on other shells).

---

## 🛠 Editor & Agent Configurations

> **💡 Command Path Placeholder**:
> In all configuration examples below, replace `<path/to/UnityCSharpLSP.Server.exe>` with the actual path to your executable (e.g. `C:/Users/username/AppData/Local/Programs/UnityCSharpLSP/UnityCSharpLSP.Server.exe`).
> If you used the Windows Installer, the directory is automatically registered in your `PATH`, so you can simply use `"UnityCSharpLSP.Server.exe"`.

### 1. OpenCode (AI Agent)

You can automatically generate the ready-to-use configuration snippet with your exact resolved path by running:

```bash
UnityCSharpLSP.Server.exe --print-config
```

Or manually configure your `opencode.json` (or `~/.config/opencode/opencode.json`):

```json
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["<path/to/UnityCSharpLSP.Server.exe>", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
```

### 2. Neovim (`nvim-lspconfig`)

In your Neovim configuration (e.g. `init.lua`):

```lua
local lspconfig = require('lspconfig')
local configs = require('lspconfig.configs')

if not configs.unity_csharp_lsp then
  configs.unity_csharp_lsp = {
    default_config = {
      cmd = { '<path/to/UnityCSharpLSP.Server.exe>', '--stdio' },
      filetypes = { 'cs' },
      root_dir = lspconfig.util.root_pattern('ProjectSettings/ProjectVersion.txt', 'Assets'),
      settings = {},
    },
  }
end

lspconfig.unity_csharp_lsp.setup({})
```

### 3. VS Code

You can use the generic [LSP Client Extension](https://marketplace.visualstudio.com/items?itemName=realsungho.simple-lsp-client) or configure a custom task:

```json
{
  "command": "<path/to/UnityCSharpLSP.Server.exe>",
  "args": ["--stdio"]
}
```

### 4. Helix / Zed

**Helix (`languages.toml`):**

```toml
[[language]]
name = "c-sharp"
language-servers = [ "unity-csharp-lsp" ]

[language-server.unity-csharp-lsp]
command = "<path/to/UnityCSharpLSP.Server.exe>"
args = ["--stdio"]
```

---

## ⚙️ CLI Options

```bash
UnityCSharpLSP.Server [options]

Options:
  --stdio                      Use standard input/output for LSP JSON-RPC communication [default: True]
  --editor-path <editor-path>  Explicit path to the Unity Editor installation (optional)
  --version                    Show version information
  -?, -h, --help               Show help and usage information
```

> **Note**: `--editor-path` is optional. UnityCSharpLSP automatically detects the installed Unity Editor version by inspecting `ProjectSettings/ProjectVersion.txt` and querying Unity Hub's registered editors list.

---

## 🧪 Testing & Verification

Run the entire test suite (unit tests and protocol E2E tests):

```bash
dotnet test
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

## 📖 Feature & Operation Reference

A complete list of the features and operations currently supported by UnityCSharpLSP.
Last updated: v0.3.1

### 1. Startup & CLI Operations

| Operation | Description |
| :--- | :--- |
| `UnityCSharpLSP.Server.exe` | Starts the LSP server over stdio (default) |
| `--stdio` | Communicates via JSON-RPC over standard input/output |
| `--editor-path <path>` | Explicitly specifies the Unity Editor installation (auto-detected when omitted) |
| `--print-config` | Prints an `opencode.json` configuration snippet for OpenCode and exits |
| `--help` | Shows help |

#### Configuration Example (OpenCode)

```json
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["<path/to/UnityCSharpLSP.Server.exe>", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
```

### 2. LSP Lifecycle

| Method | Kind | Description |
| :--- | :--- | :--- |
| `initialize` | request | Detects the project root and builds the Roslyn workspace. Advertises server capabilities |
| `initialized` | notification | Initialization-complete notification |
| `shutdown` | request | Disposes the workspace and file watchers |
| `exit` | notification | Terminates the server process |

### 3. Document Synchronization

| Method | Kind | Description |
| :--- | :--- | :--- |
| `textDocument/didOpen` | notification | Opens a file, registers it as a Roslyn document, and publishes diagnostics |
| `textDocument/didChange` | notification | Applies incremental changes in order (full replacement also supported). Re-publishes diagnostics |
| `textDocument/didSave` | notification | Applies the saved text or the latest content on disk. Re-publishes diagnostics |
| `textDocument/didClose` | notification | Closes the document, reverts to disk content, and clears diagnostics for that URI |
| `textDocument/willSave` | notification | Pre-save notification (no edits are made) |
| `textDocument/willSaveWaitUntil` | request | Returns `TextEdit[]` to apply before saving (currently an empty array) |

Sync mode: `TextDocumentSyncKind.Incremental`, position encoding: `utf-16`

### 4. Language Features (Client → Server)

| Method | Description | Implementation |
| :--- | :--- | :--- |
| `textDocument/hover` | Shows the symbol signature and XML `<summary>` as Markdown (follows the client's supported formats) | `HoverHandler.cs` |
| `textDocument/definition` | Jumps to the declaration (reference resolution across `.asmdef` boundaries) | `DefinitionHandler.cs` |
| `textDocument/typeDefinition` | Jumps to the type declaration of the symbol at the cursor | `TypeDefinitionHandler.cs` |
| `textDocument/declaration` | Jumps to the symbol declaration (including `OriginalDefinition`) | `DeclarationHandler.cs` |
| `textDocument/references` | Solution-wide reference search | `ReferencesHandler.cs` |
| `textDocument/completion` | Member completion, scope completion, and MonoBehaviour message snippet completion | `CompletionHandler.cs` |
| `completionItem/resolve` | Lazily resolves completion item `Detail` and XML documentation | `CompletionHandler.cs` |
| `textDocument/documentSymbol` | Hierarchical outline of namespaces, types, and members. Flattened to `SymbolInformation[]` when the client lacks hierarchy support | `DocumentSymbolHandler.cs` |
| `textDocument/documentHighlight` | Highlights declarations and references within the same file | `DocumentHighlightHandler.cs` |
| `textDocument/implementation` | Jumps to implementations of interfaces and abstract/virtual members | `ImplementationHandler.cs` |
| `textDocument/foldingRange` | Folding for namespaces, types, members, blocks, `#region` / `#if`, consecutive usings, and multi-line comments | `FoldingRangeHandler.cs` |
| `textDocument/selectionRange` | Returns the syntax-node hierarchy at the cursor as selection ranges | `SelectionRangeHandler.cs` |
| `textDocument/signatureHelp` | Argument signatures and XML documentation for calls, object creation, and attributes | `SignatureHelpHandler.cs` |
| `textDocument/rename` | Solution-wide rename via `Renamer` with a minimal `WorkspaceEdit` | `RenameHandler.cs` |
| `textDocument/prepareRename` | Range and placeholder for renameable tokens | `RenameHandler.cs` |
| `textDocument/prepareCallHierarchy` | Retrieves the callable symbol at the cursor | `CallHierarchyHandler.cs` |
| `callHierarchy/incomingCalls` | List of callers | `CallHierarchyHandler.cs` |
| `callHierarchy/outgoingCalls` | List of callees | `CallHierarchyHandler.cs` |
| `textDocument/prepareTypeHierarchy` | Retrieves the type item that is the starting point of the type hierarchy | `TypeHierarchyHandler.cs` |
| `typeHierarchy/supertypes` | List of base types and implemented interfaces | `TypeHierarchyHandler.cs` |
| `typeHierarchy/subtypes` | List of derived classes and implementing types | `TypeHierarchyHandler.cs` |
| `textDocument/moniker` | Document comment ID with the `csharp` scheme (falls back to the fully qualified name) | `MonikerHandler.cs` |
| `textDocument/formatting` | Formats the entire document with the Roslyn `Formatter` (honors `FormattingOptions` tab width and spaces, returns a minimal `TextEdit[]`) | `FormattingHandler.cs` |
| `textDocument/rangeFormatting` | Formats only the specified range | `FormattingHandler.cs` |
| `textDocument/onTypeFormatting` | Formats the current line when `}`, `;`, or a newline is typed | `FormattingHandler.cs` |
| `textDocument/codeAction` | Adds `using` or fully qualified names for `CS0246` / `CS0103`, and suppresses any diagnostic with `#pragma warning disable` | `CodeActionHandler.cs` |
| `codeAction/resolve` | Lazily resolves quick fixes (edits are pre-configured and returned as-is) | `CodeActionHandler.cs` |
| `textDocument/semanticTokens/full` | Encodes Roslyn classification results into LSP token format (whole document) | `SemanticTokensHandler.cs` |
| `textDocument/semanticTokens/range` | Semantic tokens for the specified range | `SemanticTokensHandler.cs` |
| `textDocument/inlayHint` | Inferred type hints for `var` (`: <type>`) and parameter-name hints for arguments (LSP 3.17) | `InlayHintHandler.cs` |
| `textDocument/codeLens` | Reference-count lenses for types and methods (`N references`) | `CodeLensHandler.cs` |
| `codeLens/resolve` | Resolves code lenses (the display command is pre-configured) | `CodeLensHandler.cs` |
| `textDocument/documentLink` | Links file paths found in comments and string literals | `DocumentLinkHandler.cs` |
| `textDocument/linkedEditingRange` | Linked editing of `#if` / `#elif` / `#define` / `#undef` symbols and `#region` / `#endregion` labels | `LinkedEditingRangeHandler.cs` |
| `textDocument/inlineValue` | Inline display of `= <value>` for constant-evaluable locals and fields (LSP 3.17) | `InlineValueHandler.cs` |
| `textDocument/diagnostic` | Pull diagnostics. Returns `unchanged` when `resultId` matches, otherwise `full` (push diagnostics continue as well) | `PullDiagnosticsHandler.cs` |
| `workspaceSymbol/resolve` | Lazily fills `containerName` / `detail` / `kind` from the `data` of symbols returned by `workspace/symbol` (custom method) | `WorkspaceSymbolHandler.cs` |

#### Client Capability Negotiation

`InitializeParams.Capabilities` received during `initialize` are stored in `ClientCapabilityStore`, and each handler switches the following behaviors:

- Hierarchical `documentSymbol` support (flat `SymbolInformation[]` when unsupported)
- Document format for hover / completion (`Markdown` / `PlainText`)
- `prepareRename` support
- `signatureHelp` context support

#### Signature Help Triggers

- `(` (start of argument list) and `,` (argument separator)
- Re-triggered on `,`

#### Completion Triggers

- `.` (member access)
- Inside classes deriving from MonoBehaviour, snippet completion of Unity messages such as `Update` / `Start` / `Awake` / `OnCollisionEnter`

#### On-Type Formatting Triggers

- `}` / `;` / newline (formats the line immediately after input with the Roslyn `Formatter`)

### 5. Workspace Operations

| Method | Kind | Description |
| :--- | :--- | :--- |
| `workspace/symbol` | request | Symbol search across all workspace folders and assemblies (no result limit, supports partial results). Uses the LSP 3.17 `WorkspaceSymbol[]` form with `data` / `tags` / `detail` |
| `workspace/diagnostic` | request | Pull-style workspace diagnostics. Returns reports for all documents with `resultId`, and returns `unchanged` when `previousResultIds` match (also supports partial result tokens) |
| `workspaceSymbol/resolve` | request | Fills in detailed information from a symbol's `data` (custom method) |
| `workspace/executeCommand` | request | `unitycsharp.reloadWorkspace` (rebuild the workspace) and `unitycsharp.showWorkspaceInfo` (returns a summary and also notifies via `window/showMessage`) |
| `workspace/didChangeConfiguration` | notification | Stores settings as a typed object. `enableDiagnostics` toggles push diagnostics on/off, and `predefinedSymbols` is also retained |
| `workspace/didChangeWatchedFiles` | notification | Detects creation, modification, and deletion of `.cs` files and reflects them in the Roslyn compilation |
| `workspace/willRenameFiles` | request | Renames types matching the old file name to the new file name via the Roslyn `Renamer`, returning solution-wide `TextEdit[]` in `WorkspaceEdit.Changes` |
| `workspace/didRenameFiles` | notification | Updates document paths after a rename (removes the old path, adds the new path) |
| `workspace/willCreateFiles` / `workspace/didCreateFiles` | request / notification | Returns edits inserting a namespace and class skeleton into a new `.cs` / adds the created `.cs` to the workspace |
| `workspace/willDeleteFiles` / `workspace/didDeleteFiles` | request / notification | Returns `DeleteFile` operations (`ignoreIfNotExists: true`) for the target URIs in `documentChanges` / removes the deleted documents from the workspace |

`initialize` advertises `executeCommandProvider.commands`, `workspace.fileOperations`, `workspaceSymbolProvider.resolveProvider: true`, `diagnosticProvider.workspaceDiagnostics: true`, and `positionEncoding: "utf-16"`.

#### Multi-Root Support

`InitializeParams.WorkspaceFolders` is accepted, and `WorkspaceRouter` creates one `UnityWorkspaceManager` per folder. File URIs are routed to the manager of the longest matching root, and `workspace/symbol` and `executeCommand` operate across all managers. When only `RootUri` / `RootPath` is provided, it behaves as a single root as before.

### 6. Server → Client Notifications

| Method | Description |
| :--- | :--- |
| `textDocument/publishDiagnostics` | Publishes Roslyn compiler diagnostics with the target document's `version`. `CS0649` for `[SerializeField]` is suppressed |
| `$/progress` | Notifies `workspace/symbol` partial results in batches of 100 as `SymbolInformation[]`. Also supports work-done progress for long-running commands (when `workDoneToken` is specified) |
| `window/showMessage` | Notifies the result of `unitycsharp.showWorkspaceInfo` as an information message |

### 7. Unity-Specific Features

| Feature | Description |
| :--- | :--- |
| Unity Editor auto-detection | Detects from Unity Hub's `editors.json`, standard install paths, and `Library/EditorInstance.json` |
| Managed DLL resolution | Automatically references `UnityEngine.dll` / `UnityEngine.*Module.dll` / `UnityEditor*.dll` |
| `.asmdef` graph | Parses GUID references, `versionDefines`, `defineConstraints`, and `includePlatforms` to build Roslyn project references |
| UPM package resolution | Parses `Packages/manifest.json`, `packages-lock.json`, and `Library/PackageCache` |
| Preprocessor symbols | Automatically defines symbols according to the Unity version, platform, and packages |
| Diagnostic suppressor | Suppresses false-positive `CS0649` warnings on `[SerializeField]` fields |
| MonoBehaviour completion | Snippet completion for Unity message methods |
| Dynamic file monitoring | Reflects new and deleted `.cs` files immediately even without opening the Unity Editor |
| No `.csproj` / `.sln` required | Builds the Roslyn workspace directly without relying on project files generated by Unity |
| Nullable | Disabled to match Unity's default (`NullableContextOptions.Disable`) |
| Multi-root workspace | Manages multiple `workspaceFolders` simultaneously as separate Roslyn workspaces |

### 8. Constraints & Known Limitations

- None in particular.
