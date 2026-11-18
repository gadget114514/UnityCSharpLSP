# Unity の Prefab/Asset を LSP が理解すると、何が嬉しいのか？

> UnityCSharpLSP の Prefab/Asset 対応について、仕組みと使い道をブログ風に解説します。

## 1. なぜ従来の C# LSP では足りないのか

Unity プロジェクトでは、GameObject や Component、SerializeField は **`.prefab` / `.unity` / `.asset` という YAML ファイル**にシリアライズされています。一方、従来の C# 言語サーバ（OmniSharp 等）は `.cs` ファイルしか見ません。

その結果:

- 「このフィールド、どこで使われている？」→ C# 側の参照は出るが、**どの Prefab が使っているかは分からない**。
- フィールド名をリネーム → **Prefab 側のシリアライズ名が古いまま**になり、Unity で Missing Script / 値消失が発生。
- `.prefab` を開いても補完・定義ジャンプ・ホバーが効かない。

つまり、C# と Asset の境界で「見えない壁」ができていたわけです。

## 2. UnityCSharpLSP の Prefab 対応の仕組み

UnityCSharpLSP は、C# の Roslyn ワークスペースに加えて、**Unity Asset 専用のデータ層**を常駐プロセスとして持ちます。

```
┌──────────────────┐        Named Pipe (StreamJsonRpc)        ┌──────────────────────┐
│  LSP Server      │  ◄────────────────────────────────────►  │  Asset Daemon        │
│  (Roslyn C#)     │                                          │  (per project root)  │
└──────────────────┘                                          └──────────────────────┘
                                                                     │
                              ┌──────────────────────────────────────┼──────────────────────────────┐
                              │                                      │                              │
                       Fast YAML Scanner                      In-Memory Graph               SQLite (WAL+mmap)
                       (zero-alloc UTF-8)                     (references)                  asset-db.sqlite
```

主な構成要素:

1. **FastUnityYamlScanner** — `.prefab/.unity/.asset` をゼロアロケーションで高速スキャンし、GameObject / Component / シリアライズフィールド / UnityEvent 呼び出しを抽出。
2. **MetaFileParser / AssetGuid** — `.meta` の GUID を解決し、C# 型と Asset を結びつける。
3. **InMemoryAssetGraph** — 「どの Asset がどの C# シンボルを参照しているか」の逆引きグラフをメモリに保持。
4. **AssetSqliteStore** — WAL + mmap の SQLite で永続化。Unity Editor が動いていなくても動作。
5. **AssetFileWatcher** — `.cs/.meta/.prefab/.unity/.asset` の変更を監視し、グラフを自動更新。
6. **Asset Daemon** — プロジェクトルートごとに 1 つだけ常駐する Named Pipe サーバ。LSP サーバが自動で起動・再利用する（手動起動も可能）。

ポイントは **Unity Editor 不要** という点。CI や AI エージェント環境でもそのまま動きます。

## 3. 使い道と例

### 3.1 Find References が Prefab を横断する

```csharp
public class Player : MonoBehaviour {
    [SerializeField] private int _maxHp;
    public void TakeDamage(int dmg) { ... }
}
```

`TakeDamage` で Find References を実行すると、C# 側の呼び出しに加えて、
「`Player.prefab` の UnityEvent から `TakeDamage` が呼ばれている」ことも表示されます。

### 3.2 リネームが YAML まで波及する

フィールド `_maxHp` を `MaxHp` にリネームすると、LSP が `.prefab` 内のシリアライズ名も更新します。さらに Code Action で `[FormerlySerializedAs("_maxHp")]` を自動追加し、既存シーンのデータを壊さない選択肢も提供します。

### 3.3 `.prefab` を直接編集できる

`.prefab` ファイルを開くと:

- **Document Symbol** で GameObject/Component 階層がツリー表示される
- **Hover** でフィールドの C# シグネチャ + XML ドキュメント + `[Tooltip]` が表示される
- **Document Link / Definition** で GUID 参照先の C# クラスへジャンプ
- **Pull Diagnostics** で Missing Script (`UNITY0001`) が報告される

### 3.4 SceneManager.LoadScene の補完

```csharp
SceneManager.LoadScene("MainScene");
```

`LoadScene("` の中で、`EditorBuildSettings.asset` に登録された有効シーン名が補完され、その `.unity` ファイルへジャンプできます。

### 3.5 AI エージェント / CI での利用

Prefab の参照グラフを LSP 経由で問い合わせられるため、AI エージェントが「このメソッドを変えたら壊れる Prefab は？」を自律的に調査できます。Unity Editor を起動する必要がないので、ヘッドレス環境でも同じ体験が得られます。

## 4. まとめ

Prefab/Asset 対応の本質は、**C# と Unity のデータ層を LSP の中で統合すること**です。これにより、リネーム・参照検索・診断・ナビゲーションが `.cs` の境界を越えて動き、Unity Editor を開かずに Unity プロジェクト全体を理解・編集できるようになります。AI エージェント時代の Unity 開発において、この「境界を越える」能力は決定的な差になります。
PY