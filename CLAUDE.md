# CLAUDE.md

ONI 実機検証ハーネス。概要と使い方は `README.md`、API の正本は `protocol.md`。ここにはエージェント向けの注意だけを書く。

## 構成
- `Gravitas.TestHarness/`: ハーネス Mod（C#）。`Http/`（サーバとエンドポイント）、`Core/`（時間制御・query・invoke・テストランナー）、`Api/`（Mod 側が使う公開 API）、`Patches/`（Harmony）、`SelfTests.cs`。
- `Gravitas.TestProbe/`: Python の probe。`probe.py`（サイクル実行）、`mods_json.py`（mods.json 確認・修復）、`mcp_server.py`（probe の MCP ラッパー。stdout は JSON-RPC 専用なので print しない）。
- 仕様書は repo 外: `F:/_System/Download/ONI 実機検証ハーネス 仕様(初版).md`（マイルストーン M1–M7。M1–M7 完了）。

- 他の Mod から使う場合は probe の `--project <Mod repo>`（Mod 側の `harness.json`）。手順は `README.md` の「他の Mod のソリューションから使う」。

## 作業ルール
- **実機を起動する操作（`probe.py run` など）は、ユーザーの本番環境の `mods.json` を書き換え、ゲームを起動する。** 依頼の範囲に含まれるか確かめ、不明なら先に聞く。
- ハーネスの挙動を変えたら `TestHarnessMod.Version` と `mod_info.yaml` の version を上げる。プロトコルを壊したら `ProtocolVersion` も上げる。probe は両方をソースと突き合わせ、古いデプロイを検出する。
- API を変えたら `protocol.md` を同時に更新する。マイルストーンの進捗は仕様書にも反映する。
- コミットは依頼されたときだけ。`git add -A` は使わず、ファイルを指定する（`Local.Build.props`、`probe.config.json`、`runs/` は追跡しない）。

## コードの落とし穴
- `mods.json` で Mod が有効かどうかは `enabledForDlc`（アクティブな DLC の ID を含むか）で決まる。`enabled` は古い互換項目で、終了時に ONI が書き換え、ゲームは見ない。`staticID` は一意ではない（Workshop 版と開発版が同じ ID を持つ）。同名のアセンブリは Mono が 1 つしか読み込まないので、Workshop 版を有効にしたまま開発版をテストしても開発版は動かない。
- probe は実行前の `mods.json` を保存して、終了後に書き戻す。恒久的な構成を変えるときだけ `python mods_json.py fix --mods-dir <mods> --id <ID> --exclusive` を使う（ゲーム停止中のみ）。
- 新しい Mod の DLL を初めて置くときは、ゲームがそれを認識して `mods.json` に登録する**前**に `mods.json` を書き換えてはいけない（未認識の Mod のエントリがあると起動中にクラッシュする）。順序は DLL 配置 → ゲームを 1 回起動して即終了 → probe。probe はエントリを追加できない（`README.md`「初回セットアップの順序」）。
- ONI にはグローバルな `Action` enum がある。デリゲートは必ず `System.Action`、時刻は `System.DateTime`。
- `UserMod2.OnLoad` の基底がすでに Harmony の `PatchAll` を実行する。もう一度呼ぶとパッチが二重に当たる。
- ゲームのメインスレッド以外から Unity API に触れない。HTTP ハンドラは `MainThreadDispatcher` 経由でメインスレッドで動く。待つ必要がある応答は `Pending` を返す。
- ゲームのコードを調べるときは `ilspycmd` で `Assembly-CSharp.dll` を逆コンパイルする。private なメンバーは `Reflect` 経由（`GenericGameSettings.disableGameOver` など）。
- Git Bash は `/ping` のような引数をパスに変換する。probe は正規化しているが、curl を直接使うときは注意。
- Windows のコンソールは cp932。出力は UTF-8 に再設定してある（`sys.stdout.reconfigure`）。

## 検証
- ビルド: `dotnet build GravitasTestHarness.sln`（Debug は ModInstallPath へコピーされる）。
- 実機確認の最小手順: `cd Gravitas.TestProbe && python probe.py run --save "05 all research.sav" --tests`（`saves/` のセーブは複製人間 0 人のサンドボックス）。
- 失敗時は `runs/<日時>/result.json` の `error`、`log_exceptions`、`crash` と `Player.log` を見る。
- 3x で 1 サイクル（600 秒）進めても誤差は 0.03 秒（実時間は約 2.8 分）。ゲーム内の速度は 3x が上限で、ハーネスも 1〜3 だけを受け付ける。
- 有人セーブ（`saves/Live Human Testing.sav`）では、複製人間のいる状態で進行・テストとも動く（サンドボックスで 1 サイクル進めると餓死する）。
- 進行中はゲームが `saves/auto_save/` に自動セーブを書く。コミットしない。
