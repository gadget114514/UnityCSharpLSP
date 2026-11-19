# 機能・操作一覧

UnityCSharpLSP が現在サポートしている機能と操作方法の一覧です。

---

## 1. 起動・CLI 操作

| 操作 | 説明 |
| :--- | :--- |
| `UnityCSharpLSP.Server.exe` | stdio で LSP サーバーを起動（デフォルト） |
| `--stdio` | 標準入出力で JSON-RPC 通信を行う |
| `--editor-path <path>` | Unity Editor のインストール先を明示指定（省略時は自動検出） |
| `--print-config` | OpenCode 用の `opencode.json` 設定スニペットを出力して終了 |
| `--asset-daemon` | プロジェクト単位の常駐 Unity Asset Data Server（Named Pipe IPC）として起動 |
| `--project-root <path>` | `--asset-daemon` 時の対象 Unity プロジェクトルート（省略時はカレントディレクトリ） |
| `--daemon-idle-minutes <int>` | `--asset-daemon` のアイドル自動終了時間（分、デフォルト `30`、`0` 以下で無効） |
| `--help` | ヘルプを表示 |

### 設定例（OpenCode）

```json
{
  "lsp": {
    "unity-csharp-2nek": {
      "command": ["<path/to/UnityCSharpLSP.Server.exe>", "--stdio"],
      "extensions": [".cs", ".prefab", ".unity", ".asset"]
    }
  }
}
```

> C# のみで十分場合は `"extensions": [".cs"]` のままでも動作します。`.prefab` / `.unity` / `.asset` を含めると YAML アセットに対する Hover / Definition / DocumentLink / DocumentSymbol / Diagnostics も有効になります。

---

## 2. LSP ライフサイクル

| メソッド | 種別 | 説明 |
| :--- | :--- | :--- |
| `initialize` | request | プロジェクトルートを検出し、Roslyn ワークスペースと Asset Data Server クライアントを構築。サーバー機能を広告 |
| `initialized` | notification | 初期化完了通知 |
| `shutdown` | request | ワークスペースとファイル監視・AssetService 接続を破棄 |
| `exit` | notification | サーバープロセスを終了 |

---

## 3. ドキュメント同期

| メソッド | 種別 | 説明 |
| :--- | :--- | :--- |
| `textDocument/didOpen` | notification | ファイルを開き、Roslyn ドキュメントとして登録。C# / YAML アセットの診断を公開 |
| `textDocument/didChange` | notification | 増分変更を順番に反映（全文置換も対応）。診断を再公開 |
| `textDocument/didSave` | notification | 保存テキスト、またはディスク上の最新内容を反映。診断を再公開 |
| `textDocument/didClose` | notification | ドキュメントを閉じてディスク内容に戻し、その URI の診断をクリア |
| `textDocument/willSave` | notification | 保存直前の通知（編集は行わない） |
| `textDocument/willSaveWaitUntil` | request | 保存前に適用する `TextEdit[]` を返す（現在は空配列） |

同期方式: `TextDocumentSyncKind.Incremental`、位置エンコーディング: `utf-16`

---

## 4. 言語機能（クライアント → サーバー）

| メソッド | 説明 | 実装 |
| :--- | :--- | :--- |
| `textDocument/hover` | C# シンボルのシグネチャと XML `<summary>` を表示。`.prefab` / `.unity` / `.asset` 上では対象アセット/フィールド情報と対応する C# シグネチャ・XML doc・`[Tooltip]` を統合表示 | `HoverHandler.cs` |
| `textDocument/definition` | 宣言へジャンプ（`.asmdef` 境界を越えた参照解決）。`.prefab` / `.unity` / `.asset` の `guid:` やシリアライズフィールド/`propertyPath` から対象ファイル・C# フィールドへジャンプ。C# 内の `SceneManager.LoadScene("...")` / `LoadSceneAsync("...")` から `.unity` シーンへジャンプ | `DefinitionHandler.cs` |
| `textDocument/typeDefinition` | カーソル位置のシンボルの型宣言へジャンプ | `TypeDefinitionHandler.cs` |
| `textDocument/declaration` | シンボルの宣言（`OriginalDefinition` 含む）へジャンプ | `DeclarationHandler.cs` |
| `textDocument/references` | ソリューション全体の C# 参照に加え、`MonoBehaviour` / `ScriptableObject` 型・シリアライズフィールド・`UnityEvent` 呼び出しメソッドに対する `.prefab` / `.unity` / `.asset` 内の直接・間接参照を列挙 | `ReferencesHandler.cs` |
| `textDocument/completion` | メンバー補完、スコープ補完、MonoBehaviour メッセージのスニペット補完、および `SceneManager.LoadScene("` / `LoadSceneAsync("` 内での有効ビルドシーン名（`SceneName` / `RelativePath`）補完 | `CompletionHandler.cs` |
| `completionItem/resolve` | 補完項目の `Detail` と XML ドキュメントを遅延解決 | `CompletionHandler.cs` |
| `textDocument/documentSymbol` | C# の名前空間・型・メンバーの階層アウトライン。`.prefab` / `.unity` では `GameObject` / `PrefabInstance` / コンポーネントの Hierarchy ツリーを返却 | `DocumentSymbolHandler.cs` |
| `textDocument/documentHighlight` | 同ファイル内の宣言・参照をハイライト | `DocumentHighlightHandler.cs` |
| `textDocument/implementation` | インターフェース／抽象・virtual メンバーの実装へジャンプ | `ImplementationHandler.cs` |
| `textDocument/foldingRange` | 名前空間・型・メンバー・ブロック・`#region`／`#if`・連続 using・複数行コメントの折りたたみ | `FoldingRangeHandler.cs` |
| `textDocument/selectionRange` | カーソル位置の構文ノード階層を選択範囲として返す | `SelectionRangeHandler.cs` |
| `textDocument/signatureHelp` | 呼び出し・オブジェクト生成・属性の引数シグネチャと XML ドキュメント | `SignatureHelpHandler.cs` |
| `textDocument/rename` | `Renamer` によるソリューション全体のリネームに加え、Unity シリアライズフィールド・メソッドの変更を `.prefab` / `.unity` / `.asset` の YAML キー・`propertyPath`・`m_MethodName` へマージ | `RenameHandler.cs` |
| `textDocument/prepareRename` | リネーム可能なトークンの範囲とプレースホルダー | `RenameHandler.cs` |
| `textDocument/prepareCallHierarchy` | カーソル位置の呼び出し可能シンボルを取得 | `CallHierarchyHandler.cs` |
| `callHierarchy/incomingCalls` | 呼び出し元（caller）一覧 | `CallHierarchyHandler.cs` |
| `callHierarchy/outgoingCalls` | 呼び出し先（callee）一覧 | `CallHierarchyHandler.cs` |
| `textDocument/prepareTypeHierarchy` | 型階層の起点となる型アイテムを取得 | `TypeHierarchyHandler.cs` |
| `typeHierarchy/supertypes` | 基底型・実装インターフェース一覧 | `TypeHierarchyHandler.cs` |
| `typeHierarchy/subtypes` | 派生クラス・実装型一覧 | `TypeHierarchyHandler.cs` |
| `textDocument/moniker` | `csharp` スキームのドキュメントコメント ID（フォールバックは完全修飾名） | `MonikerHandler.cs` |
| `textDocument/formatting` | Roslyn `Formatter` でドキュメント全体を整形（`FormattingOptions` のタブ幅・スペースを反映し、最小 `TextEdit[]` を返す） | `FormattingHandler.cs` |
| `textDocument/rangeFormatting` | 指定範囲のみを整形 | `FormattingHandler.cs` |
| `textDocument/onTypeFormatting` | `}` / `;` / 改行の入力時に現在行を整形 | `FormattingHandler.cs` |
| `textDocument/codeAction` | `CS0246` / `CS0103` に対する `using` 追加・完全修飾名、全診断に対する `#pragma warning disable` 抑制、Unity シリアライズフィールドへの `Add [FormerlySerializedAs("<fieldName>")]` リファクタリング | `CodeActionHandler.cs` |
| `codeAction/resolve` | クイックフィックスの遅延解決（編集は事前設定のためそのまま返す） | `CodeActionHandler.cs` |
| `textDocument/semanticTokens/full` | Roslyn の分類結果を LSP トークン形式にエンコード（全ドキュメント） | `SemanticTokensHandler.cs` |
| `textDocument/semanticTokens/range` | 指定範囲のセマンティックトークン | `SemanticTokensHandler.cs` |
| `textDocument/inlayHint` | `var` の推論型ヒント（`: <type>`）と引数のパラメータ名ヒント（LSP 3.17） | `InlayHintHandler.cs` |
| `textDocument/codeLens` | 型・メソッドの参照カウントレンズ（`MonoBehaviour` / `ScriptableObject` 型では `N references (P prefabs, S scenes)` とアセット参照位置を表示） | `CodeLensHandler.cs` |
| `codeLens/resolve` | コードレンズの解決（表示コマンドは事前設定） | `CodeLensHandler.cs` |
| `textDocument/documentLink` | C# コメント・文字列リテラル内のファイルパス、C# 内の `SceneManager.LoadScene` / `LoadSceneAsync` シーン名、および `.prefab` / `.unity` / `.asset` 内の `guid:` 参照をリンク | `DocumentLinkHandler.cs` |
| `textDocument/linkedEditingRange` | `#if` / `#elif` / `#define` / `#undef` のシンボル、`#region` / `#endregion` のラベルを連動編集 | `LinkedEditingRangeHandler.cs` |
| `textDocument/inlineValue` | 定数評価可能なローカル・フィールドに `= <value>` をインライン表示（LSP 3.17） | `InlineValueHandler.cs` |
| `textDocument/diagnostic` | pull 型診断。C# 診断に加え `.prefab` / `.unity` / `.asset` の Missing Script (`UNITY0001`)、opt-in の Dangling Asset Reference (`UNITY0002`)、opt-in の Prefab Override Impact (`UNITY0003`)、opt-in の Unattached Script (`UNITY0004`、`Hint`) を報告。`resultId` が一致すれば `unchanged` を返す | `PullDiagnosticsHandler.cs` |
| `workspaceSymbol/resolve` | `workspace/symbol` が返したシンボルの `data` から `containerName` / `detail` / `kind` を遅延補完（カスタムメソッド） | `WorkspaceSymbolHandler.cs` |

### クライアント機能交渉

`initialize` で受け取った `InitializeParams.Capabilities` を `ClientCapabilityStore` に保存し、各ハンドラーが以下を切り替えます。

- 階層 `documentSymbol` 対応（未対応時はフラットな `SymbolInformation[]`）
- hover / completion のドキュメント形式（`Markdown` / `PlainText`）
- `prepareRename` 対応
- `signatureHelp` のコンテキスト対応

### シグネチャヘルプのトリガー

- `(`（引数リスト開始）と `,`（引数区切り）
- `,` で再トリガー

### 補完トリガー

- `.`（メンバーアクセス）
- `"`（`SceneManager.LoadScene("` / `LoadSceneAsync("` 内のビルドシーン補完）
- MonoBehaviour 継承クラス内では `Update` / `Start` / `Awake` / `OnCollisionEnter` などの Unity メッセージをスニペット補完

### オンタイム整形トリガー

- `}` / `;` / 改行（入力直後の行を Roslyn `Formatter` で整形）

---

## 5. ワークスペース操作

| メソッド | 種別 | 説明 |
| :--- | :--- | :--- |
| `workspace/symbol` | request | 全ワークスペースフォルダ・全アセンブリ横断のシンボル検索（件数制限なし、部分結果通知に対応）。LSP 3.17 の `WorkspaceSymbol[]` 形式で `data` / `tags` / `detail` を付与 |
| `workspace/diagnostic` | request | pull 型のワークスペース診断。全ドキュメントのレポートを `resultId` 付きで返し、`previousResultIds` が一致すれば `unchanged` を返す（部分結果トークンにも対応） |
| `workspaceSymbol/resolve` | request | シンボルの `data` から詳細情報を補完（カスタムメソッド） |
| `workspace/executeCommand` | request | カスタムコマンドを実行（下記一覧を参照） |
| `workspace/didChangeConfiguration` | notification | 設定を typed object に保持。`enableDiagnostics` で push 診断の有効/無効を切り替え、`predefinedSymbols`、`propagateRenamesToAssets`、`reportDanglingAssetReferences`、`reportPrefabOverrideImpact`、`reportUnattachedScripts` を保持（snake_case エイリアスも可） |
| `workspace/didChangeWatchedFiles` | notification | `.cs` ファイルの作成・変更・削除を検出し、Roslyn コンパイルへ反映 |
| `workspace/willRenameFiles` | request | 旧ファイル名と同名の型を Roslyn `Renamer` で新ファイル名へリネームし、ソリューション全体の `TextEdit[]` を `WorkspaceEdit.Changes` で返す |
| `workspace/didRenameFiles` | notification | リネーム後のドキュメントパスを更新（旧パス削除・新パス追加） |
| `workspace/willCreateFiles` / `workspace/didCreateFiles` | request / notification | 新しい `.cs` に名前空間とクラスのひな形を挿入する編集を返す／作成後の `.cs` をワークスペースへ追加 |
| `workspace/willDeleteFiles` / `workspace/didDeleteFiles` | request / notification | 削除対象 URI の `DeleteFile` 操作（`ignoreIfNotExists: true`）を `documentChanges` で返す／削除後のドキュメントをワークスペースから除去 |

### `workspace/executeCommand` 対応コマンド

| コマンド名 | 引数 (`arguments[0]`) | 説明 |
| :--- | :--- | :--- |
| `unitycsharp.reloadWorkspace` | なし | 全ワークスペースフォルダの Roslyn ワークスペースを再構築し、AssetServer の差分再スキャン (`RescanAsync`) を実行 |
| `unitycsharp.showWorkspaceInfo` | なし | ワークスペース数・プロジェクト数・ドキュメント数の概要を返し、`window/showMessage` でも通知 |
| `unitycsharp.getSceneOrPrefabHierarchy` | `{ path, maxDepth?, includeSerializedValues? }` | `.unity` / `.prefab` の GameObject・PrefabInstance・コンポーネント階層ツリー（`AssetHierarchyNodeDto[]`）を取得（`maxDepth` 既定 `16`、`includeSerializedValues` 既定 `true`） |
| `unitycsharp.findSymbolUsagesInAssets` | `{ scriptGuid?, symbolName?, memberKind?, memberName?, includeTransitiveScenes?, maxResults? }` | 指定スクリプト（`scriptGuid` または `symbolName`）やメンバー（`Class` / `Field` / `Method`）の Prefab / Scene 内参照箇所を検索 |
| `unitycsharp.assetServerStatus` | `{ path?, projectRoot? }`（省略可） | AssetServer のインデックス状態・登録アセット数・DB パス (`DaemonStatusResponse`) を取得 |

`initialize` では `executeCommandProvider.commands`、`workspace.fileOperations`、`workspaceSymbolProvider.resolveProvider: true`、`diagnosticProvider.workspaceDiagnostics: true`、`positionEncoding: "utf-16"` を広告します。

### マルチルート対応

`InitializeParams.WorkspaceFolders` を受け取り、`WorkspaceRouter` がフォルダごとに `UnityWorkspaceManager` を生成します。ファイル URI は最長一致ルートのマネージャへルーティングされ、`workspace/symbol` や `executeCommand` は全マネージャを対象に動作します。`RootUri` / `RootPath` のみの場合は従来どおり単一ルートとして動作します。

---

## 6. サーバー → クライアント通知

| メソッド | 説明 |
| :--- | :--- |
| `textDocument/publishDiagnostics` | Roslyn コンパイラ診断と `.prefab` / `.unity` / `.asset` の Missing Script 診断 (`UNITY0001`) に加え、opt-in の `UNITY0002` / `UNITY0003` と C# 側の opt-in `UNITY0004` (`Hint`) を公開。`[SerializeField]` の `CS0649` と `UnityEvent` 参照メソッドの `IDE0051` は抑制 |
| `$/progress` | `workspace/symbol` の部分結果を 100 件ずつ `SymbolInformation[]` で通知。長時間コマンドの work-done 進捗（`workDoneToken` 指定時）にも対応 |
| `window/showMessage` | `unitycsharp.showWorkspaceInfo` の結果を情報メッセージで通知 |

---

## 7. Unity 固有機能

| 機能 | 説明 |
| :--- | :--- |
| Unity Editor 自動検出 | Unity Hub の `editors.json`、標準インストールパス、`Library/EditorInstance.json` から検出 |
| Managed DLL 解決 | `UnityEngine.dll` / `UnityEngine.*Module.dll` / `UnityEditor*.dll` を自動参照 |
| `.asmdef` グラフ | GUID 参照・`versionDefines`・`defineConstraints`・`includePlatforms` を解析し、Roslyn プロジェクト参照を構築 |
| UPM パッケージ解決 | `Packages/manifest.json` と `packages-lock.json`、`Library/PackageCache` を解析 |
| プリプロセッサシンボル | Unity バージョン・プラットフォーム・パッケージに応じたシンボルを自動定義 |
| 診断サプレッサー | `[SerializeField]` フィールドの `CS0649` および `UnityEvent` 参照メソッドの `IDE0051` 誤警告を抑制 |
| 未アタッチスクリプト診断 | opt-in (`reportUnattachedScripts`、既定 `false`) で、具象 `MonoBehaviour` / `ScriptableObject` のうち prefab / scene に一度もアタッチされない型へ C# 宣言上に `UNITY0004` (`Hint`) を報告。abstract / interface / ジェネリック / エディタ限定クラスは対象外 |
| MonoBehaviour 補完 | Unity メッセージメソッドのスニペット補完 |
| Prefab / Scene 双方向 LSP | 常駐 Asset Data Daemon (`--asset-daemon`) と SQLite WAL+mmap (`Library/UnityCSharpLSP/asset-db.sqlite`) + オンメモリグラフにより、Prefab/Variant/Scene 間の参照検索・CodeLens・リネーム伝播・YAML ナビゲーション・Missing Script 診断・opt-in の Prefab Override Impact 診断・`SceneManager.LoadScene` 補完/ジャンプを提供 |
| 動的ファイル監視 | Unity Editor を開かなくても新規・削除 `.cs` および `.prefab` / `.unity` / `.asset` / `.meta` を即時反映 |
| `.csproj` / `.sln` 不要 | Unity が生成するプロジェクトファイルに依存せず、直接 Roslyn ワークスペースを構築 |
| Nullable | Unity デフォルトに合わせて無効 (`NullableContextOptions.Disable`) |
| マルチルートワークスペース | 複数 `workspaceFolders` を個別の Roslyn ワークスペースとして同時に管理 |

---

## 8. 制約・既知の制限

- 特になし。
