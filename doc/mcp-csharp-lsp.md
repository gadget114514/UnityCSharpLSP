# MCP サーバー計画: mcp-csharp-lsp

UnityCSharpLSP の LSP 機能を、MCP (Model Context Protocol) のツールとして LLM エージェントに公開する計画。

## 1. 方針

- **LSP をそのまま見せる。** ツール名は LSP メソッド名に 1:1 対応させ、引数・戻り値は LSP の JSON をそのまま使う。MCP 独自の抽象化（「シンボル名で検索して参照を返す」等の高水準ツール）は作らない。
- **既存ハンドラーを一切書き換えない。** MCP 層は `LspJsonRpcSession` の前に立つ薄いブリッジとし、LSP の機能追加はそのまま MCP にも反映される。
- **例外は最小限。** エージェントが LSP クライアントの責務（ドキュメント同期、initialize、URI 変換）を負えないので、その部分だけブリッジが肩代わりする。

## 2. アーキテクチャ

```
MCP client (Claude Code 等)
   │  MCP / stdio (JSON-RPC, newline-delimited)
   ▼
McpBridge  ─ tools/list, tools/call を LSP リクエストに変換
   │  StreamJsonRpc (HeaderDelimited) over Nerdbank.Streams FullDuplexStream  ← in-process
   ▼
LspJsonRpcSession (既存・無改造)  → Handlers → UnityWorkspaceManager / AssetServer daemon
```

- 起動: `UnityCSharpLSP.Server --mcp [--workspace <path>]`（既存 exe にモードを追加。別 exe は作らない）。
- in-process 接続は `tests/UnityCSharpLSP.Protocol.Tests/LspSessionTests.cs` の `CreateSessionPair()` と同じ方式（`FullDuplexStream.CreatePair()` + `HeaderDelimitedMessageHandler`）。
- MCP 側の実装は公式 C# SDK (`ModelContextProtocol` NuGet) の stdio トランスポートを使う。ツールは属性ではなく、起動時にツール表から動的登録する（§4 の表がそのままコードのデータになる）。
- stdout は MCP 専用。ログは従来どおり stderr。

## 3. ブリッジが肩代わりする LSP クライアント責務

| 責務 | 振る舞い |
| :--- | :--- |
| `initialize` / `initialized` | MCP 起動時に 1 回送る。`workspaceFolders` は `--workspace`（省略時は CWD から `Assets/` か `ProjectSettings/` を持つ祖先）。`capabilities` は階層 documentSymbol・Markdown hover・LSP 3.17 機能をすべて「対応」と申告する |
| ドキュメント同期 | `textDocument.uri` を含む呼び出しの前に、未オープンならディスク内容で `didOpen`、オープン済みで mtime/サイズが変わっていれば全文 `didChange`。エージェントはファイルを直接編集する前提なので `didClose` は送らない（必要なら `lsp_reset` で全クローズ） |
| URI | `textDocument.uri` に絶対パス・ワークスペース相対パスも受け付け、`file:///` に正規化する。戻り値の URI はそのまま（変換しない） |
| 位置 | **LSP どおり 0-based / UTF-16**。変換しない。ツール説明に明記する |
| サーバー→クライアント | `textDocument/publishDiagnostics` はキャッシュして `lsp_diagnostics_cached` で読めるようにする（任意）。`window/showMessage`・`$/progress` は stderr ログに流す。`workspace/configuration` 等のリクエストには空応答 |
| 設定 | `workspace/didChangeConfiguration` をそのままツール化（§4）。起動時の既定値は `--settings <json>` で渡せる |
| shutdown | MCP セッション終了で `shutdown` / `exit` |

## 4. ツール一覧

ツール名 = LSP メソッド名の `/` を `_` に置換したもの。`arguments` = LSP の params オブジェクト、`structuredContent`/`content[0].text` = LSP の result JSON。

### 4.1 読み取り系（`readOnlyHint: true`）

| MCP ツール | LSP メソッド |
| :--- | :--- |
| `textDocument_hover` | `textDocument/hover` |
| `textDocument_definition` | `textDocument/definition` |
| `textDocument_declaration` | `textDocument/declaration` |
| `textDocument_typeDefinition` | `textDocument/typeDefinition` |
| `textDocument_implementation` | `textDocument/implementation` |
| `textDocument_references` | `textDocument/references`（拡張引数 `includePatterns` / `excludePatterns` もそのまま通る） |
| `textDocument_documentSymbol` | `textDocument/documentSymbol` |
| `textDocument_documentHighlight` | `textDocument/documentHighlight` |
| `textDocument_signatureHelp` | `textDocument/signatureHelp` |
| `textDocument_completion` | `textDocument/completion` |
| `completionItem_resolve` | `completionItem/resolve` |
| `textDocument_diagnostic` | `textDocument/diagnostic` |
| `workspace_diagnostic` | `workspace/diagnostic` |
| `workspace_symbol` | `workspace/symbol` |
| `workspaceSymbol_resolve` | `workspaceSymbol/resolve` |
| `textDocument_prepareCallHierarchy` | `textDocument/prepareCallHierarchy` |
| `callHierarchy_incomingCalls` / `callHierarchy_outgoingCalls` | 同名 |
| `textDocument_prepareTypeHierarchy` | `textDocument/prepareTypeHierarchy` |
| `typeHierarchy_supertypes` / `typeHierarchy_subtypes` | 同名 |
| `textDocument_codeLens` | `textDocument/codeLens` |
| `textDocument_documentLink` | `textDocument/documentLink` |
| `textDocument_inlayHint` | `textDocument/inlayHint` |
| `textDocument_moniker` | `textDocument/moniker` |
| `textDocument_prepareRename` | `textDocument/prepareRename` |

### 4.2 編集提案系（`WorkspaceEdit` / `TextEdit[]` を返すだけ。ファイルは書き換えない）

| MCP ツール | LSP メソッド |
| :--- | :--- |
| `textDocument_rename` | `textDocument/rename`（アセット YAML への伝播を含む） |
| `textDocument_codeAction` / `codeAction_resolve` | 同名 |
| `textDocument_formatting` / `textDocument_rangeFormatting` | 同名 |
| `workspace_willRenameFiles` / `workspace_willCreateFiles` / `workspace_willDeleteFiles` | 同名 |

### 4.3 コマンド・設定

| MCP ツール | 内容 |
| :--- | :--- |
| `workspace_executeCommand` | `workspace/executeCommand` そのもの（`unitycsharp.*` 全コマンド。`command` の enum は `ExecuteCommandHandler.Commands` から生成） |
| `workspace_didChangeConfiguration` | 通知を送り `null` を返す |
| `lsp_reset` | ブリッジ独自。全ドキュメント `didClose` + `unitycsharp.reloadWorkspace` |

### 4.4 公開しないもの

`semanticTokens/*`, `foldingRange`, `selectionRange`, `linkedEditingRange`, `inlineValue`, `onTypeFormatting`, `codeLens/resolve`, `textDocument/didSave` 系 — 表示用途でエージェントに価値が薄い。ツール表に 1 行足せば公開できる構造にしておく。

### 4.5 編集の適用について

素直さを優先し、**`WorkspaceEdit` を適用するツールは作らない**（エージェントが自分のファイル編集ツールで適用する）。ただしリネームはアセット YAML を含み手作業適用がつらいので、要望次第で後続フェーズで `workspace_applyEdit`（ブリッジ独自、`WorkspaceEdit` を受けてディスクへ書き込み → 同期）を追加する。

## 5. スキーマ

- `inputSchema` は手書きせず、`Microsoft.VisualStudio.LanguageServer.Protocol` の params 型（`TextDocumentPositionParams`, `ReferenceParams` 等）から起動時に生成する。拡張フィールド（`includePatterns` 等）はツール表で追記。
- 生成が重い／不正確な型は、ツール表で手書きスキーマに差し替え可能にする。
- 説明文は LSP 仕様の 1 行要約 + 「position は 0-based」「uri はパス可」の共通注記。

## 6. エラー

- LSP の `ResponseError` は MCP の `isError: true` + `content` に `{code, message}` を入れて返す（プロトコルエラーにはしない）。
- `null` 結果はそのまま `null`（"見つからない" を表す LSP の意味を保つ）。
- ワークスペースロード中の呼び出しは、既存ハンドラーの待機挙動に任せる。ブリッジはタイムアウト（既定 120 秒、`--timeout`）だけ持つ。

## 7. 実装ステップ

1. **ブリッジ骨格**: `src/UnityCSharpLSP.Mcp/`（ライブラリ）を追加。`LspLoopbackClient`（FullDuplexStream で `LspJsonRpcSession` と接続、initialize、サーバー発リクエスト/通知の受け口）。
2. **ドキュメント同期**: `DocumentSyncTracker`（uri → version, mtime, size）。§3 のルール実装。
3. **ツール表**: `LspToolTable`（ツール名・LSP メソッド・params 型・readOnly フラグ・追加スキーマ）。§4 の表をこのデータとして持つ。
4. **MCP ホスト**: `ModelContextProtocol` SDK で stdio サーバーを立て、ツール表から `tools/list` / `tools/call` を登録。
5. **Server へのモード追加**: `src/UnityCSharpLSP.Server/Program.cs` に `--mcp`, `--workspace`, `--settings`, `--timeout`。`--print-config --mcp` で Claude Code / OpenCode 用の MCP 設定スニペットを出力。
6. **テスト**: `tests/UnityCSharpLSP.Mcp.Tests/` — DummyProject に対して `tools/list` の件数・スキーマ、`textDocument_definition` / `textDocument_references` / `workspace_executeCommand(unitycsharp.findSymbolUsagesInAssets)` の結果が直接 LSP 呼び出しと一致すること、ディスク編集後の再同期。
7. **ドキュメント・配布**: `README.md` と `doc/functions.md` に MCP 節を追加、`installer.iss` は exe が同じなので変更不要（設定例のみ追記）。

## 8. 設定例（予定）

```json
{
  "mcpServers": {
    "unity-csharp-lsp": {
      "command": "UnityCSharpLSP.Server.exe",
      "args": ["--mcp", "--workspace", "D:/MyUnityProject"]
    }
  }
}
```

## 9. 未決事項

- `workspace_applyEdit` を初期スコープに入れるか（§4.5）。
- position を 1-based でも受けるオプション（`lsp-refs` は 1-based）を設けるか。現状案は LSP 準拠の 0-based のみ。
- ツール数（約 40）がクライアントのツール上限・コンテキストに対して多すぎる場合、`--tools <glob>` で公開ツールを絞る機能を付けるか。
