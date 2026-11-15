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
- **👀 Dynamic File Monitoring**:
  - Built-in file watcher and `workspace/didChangeWatchedFiles` handler automatically detect newly created, modified, or deleted `.cs` files and update the Roslyn compilation in real-time.
- **📦 Single-File Binary**:
  - Bundled with all Roslyn and JSON-RPC dependencies into a single, standalone executable.

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

## 🛠 Editor & Agent Configurations

### 1. OpenCode (AI Agent)

Add the following to your `opencode.json` (or `~/.config/opencode/opencode.json`):

```json
{
  "lsp": {
    "unity-csharp": {
      "command": ["UnityCSharpLSP.Server.exe", "--stdio"],
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
      cmd = { 'UnityCSharpLSP.Server.exe', '--stdio' },
      filetypes = { 'cs' },
      root_dir = lspconfig.util.root_pattern('ProjectSettings/ProjectVersion.txt', 'Assets'),
      settings = {},
    },
  }
end

lspconfig.unity_csharp_lsp.setup({})
```

### 3. VS Code

You can use the generic [LSP Client Extension](https://marketplace.visualstudio.com/items?itemName=realsungho.simple-lsp-client) or configure a custom task, pointing the server command to:

```json
{
  "command": "UnityCSharpLSP.Server.exe",
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
command = "UnityCSharpLSP.Server.exe"
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
