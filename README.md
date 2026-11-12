# Gravitas.TestHarness

ONI (Oxygen Not Included) の実機検証ハーネス。ゲーム内に HTTP JSON API を持つ Mod（ハーネス）を入れ、外部スクリプト（probe）が
ビルド → デプロイ → 起動 → セーブのロード → 検証 → 終了までを自動で回し、結果を JSON で返す。

| ディレクトリ | 役割 |
| --- | --- |
| `Gravitas.TestHarness/` | ハーネス Mod（C#, netstandard2.1, Harmony）。127.0.0.1 で HTTP API を提供する |
| `Gravitas.TestProbe/` | 外部の probe（Python 3、標準ライブラリのみ）。1 回の検証サイクルを実行して JSON を出力する |
| `saves/` | 検証用セーブ（小さく保つ）。`probe.py run --save` で指定する |
| `protocol.md` | HTTP プロトコル v1、起動引数、テストの書き方（API の正本） |

## セットアップ

1. `Gravitas.TestHarness/Local.Build.props.example` を `Local.Build.props` にコピーし、`GameManagedPath`（`OxygenNotIncluded_Data/Managed`）と
   `ModInstallPath`（`Documents/Klei/OxygenNotIncluded/mods/local/Gravitas.TestHarness`）を自分の環境に合わせる。
2. `Gravitas.TestProbe/probe.config.example.json` を `probe.config.json` にコピーして編集する（`game_dir`、`mods_dir`、`steam_exe`、`port` など）。
3. ビルドする: `dotnet build GravitasTestHarness.sln`（Debug は Mod フォルダへ自動コピーされる。Release は `release/` に出力）。
4. ゲームのモード管理画面でハーネスを一度有効化しておく（以後 probe が `mods.json` を毎回確認・修復する）。有効かどうかは `enabledForDlc` で決まる（`enabled` は無視される）。

## 使い方

```
cd Gravitas.TestProbe
python probe.py run                               # build -> mods.json -> 起動 -> 待機 -> バージョン確認 -> 終了
python probe.py run --save "05 all research.sav"  # saves/ のセーブを自動ロードして playing まで待つ
python probe.py run --save X.sav --steps '[["POST","/time/advance",{"ticks":50}],["POST","/query",{"path":"GameClock.Instance.cycle"}]]'
python probe.py run --save X.sav --tests          # ゲーム内テスト（[HarnessTest]）を全件実行
python probe.py run --save X.sav --tests "heater" # id のパターン指定（JSON で names/tags/timeout_s も指定可）
python probe.py run --keep                        # ゲームを残す（手動確認用）
python probe.py http GET /status                  # 起動中のゲームの API を直接叩く
python probe.py fix-mods                          # mods.json の確認・修復のみ
```

結果は標準出力に JSON で出る（`ok`、`stage`、`error`、`steps`、`tests`、`log_exceptions`、失敗時は `crash`）。終了コードは成功 0 / 失敗 1。
成果物は `Gravitas.TestProbe/runs/<日時>/`（`result.json`、`Player.log`、`build.log`、`screenshots/`）に残り、直近 10 回分を保持する。

## 自分の Mod をテストする

テストは検証したい Mod 側に書く。static で引数なしのメソッドに `[HarnessTest]` を付けるだけで、ハーネスが全アセンブリから探して実行する。

```csharp
[HarnessTest(Name = "mymod.heater_warms", Tags = "heat")]
public static IEnumerator HeaterWarms()
{
    var before = ReadTemperature();
    yield return Wait.Cycles(1);
    Check.AtLeast(before + 1, ReadTemperature());
}
```

詳細（戻り値、`Wait`/`Check`、タグ、結果の種類）は `protocol.md` の「In-game tests」を参照。
検証対象の Mod を `probe.config.json` の `required_mods` に Mod ID で書くと、probe が `mods.json` でそれらを有効にしてから起動する。実行が終わってゲームが終了したら、probe は実行前の `mods.json` を**そのまま書き戻す**（ユーザーの有効 Mod 構成を変えない）。`--keep` のときは書き戻さないので、終了後に `fix-mods` か `mods.json.probe-backup-*` で戻す。

**セーフモード**: 起動後にゲームが Mod セーフモードで立ち上がると（直前の起動が読み込み中に死んだ場合）、ハーネスが読み込まれず応答しない。probe はログの `Entering mod safe mode` を見て、タイムアウトを待たずに `stage: safe-mode` で止める（実行前に `mod_load_in_progress` も直す）。

## 他の Mod のソリューションから使う

probe とハーネスはこのリポジトリに置いたまま、検証したい Mod 側に `harness.json` を置いて `--project` で指すだけでよい（Mod 側に Python や設定の複製は要らない）。

```
python <このrepo>/Gravitas.TestProbe/probe.py run --project <Mod repo> --tests
```

`harness.json`（Mod repo 直下。パスは `/` 区切りで書く）:

```json
{
  "build": ["dotnet build MyMod.sln -v q -p:ModInstallPath=<mods>/local/MyMod"],
  "required_mods": ["MyMod.Local"],
  "save": "my-test.sav",
  "saves_dir": "saves"
}
```

- `build`: Mod repo のフォルダで実行する。ハーネス自身のビルドは probe が先に行う（`"build_harness": false` で省略）。`ModInstallPath` が実際にゲームの読む mods フォルダを指しているか確認すること。
- `required_mods`: 有効化しておく Mod の staticID（ローカル Mod は `<フォルダ名>.Local`）。ハーネスは常に含まれる。
- `save` / `saves_dir`: セーブは `saves_dir`（Mod repo 基準）、無ければこのリポジトリの `saves/` から探す。
- 実行ごとの成果物は Mod repo の `.harness-runs/`（直近 10 回）に出る。`.gitignore` に追加すること。
- マシン固有の設定（`game_dir`、`mods_dir`、`steam_exe`）は probe 側の `probe.config.json` に 1 回だけ書く。

### 初回セットアップの順序（新しい Mod を初めて置くとき）

**DLL を置く前、またはゲームに Mod を認識させる前に `mods.json` を書き換えてはいけない。** ゲームは起動時に `mods.json` を読み、そのあとで mods フォルダの新しい Mod を見つけて `mods.json` に追加する。先に `mods.json` へ書くと、「まだ認識されていない Mod」のエントリがある状態になり、起動途中でクラッシュする。

1. Mod の DLL と `mod.yaml` / `mod_info.yaml` を、ゲームの読む mods フォルダ（`.../mods/local/<名前>`）にビルドして置く。この時点では `mods.json` に触れない（通常の `run` も `fix-mods` も実行しない）。
2. `python <このrepo>/Gravitas.TestProbe/probe.py run --project <Mod repo> --setup` を実行する。probe がビルドし、`mods.json` に触れずにゲームを起動して、`required_mods` とハーネスが `mods.json` に登録されるのを待ち、ゲームを閉じる（セーブのロードもテストもしない）。ゲームを手で起動してすぐ終了しても同じ。
3. そのあとで通常の `run` を実行する（`mods.json` の有効化は probe が行う）。

`--setup` は `mods.json` を書き換えない（ゲーム自身が登録のために書く）。登録の判定は `mods.json` に `required_mods` と `Gravitas.TestHarness.Local` の staticID があること（ローカル Mod は `<フォルダ名>.Local`）。`ready_timeout_s` 内に揃わなければ、足りない ID を `error` に出す。probe は通常の `run` ではエントリを追加できない（有効・無効を切り替えるだけ）。ハーネス自身を初めて置くときも同じ。登録済みの Mod の DLL を更新するだけなら、この手順は不要。

テストは Mod 側に `#if DEBUG` で書く。ハーネスの DLL を参照すれば `Check` / `Wait` が使える
（csproj に `<Reference Include="Gravitas.TestHarness" Condition="'$(Configuration)'=='Debug'"><HintPath><このrepo>/Gravitas.TestHarness/bin/Debug/netstandard2.1/Gravitas.TestHarness.dll</HintPath><Private>false</Private></Reference>`）。
参照したくない場合は、`HarnessTestAttribute` と `HarnessAssertionException` を Mod 内に自前で定義すれば名前で認識される
（`CaffeineIncluded/CaffeineIncluded/HarnessTests.cs` が例。時間を進めるテスト（`Wait`）だけは参照が必要）。
`--tests` は、そのとき読み込まれている全テスト（ハーネスの自己テスト `harness.*` を含む）を実行する。自分のテストだけなら `--tests '{"tags":["mymod"]}'` のようにタグで絞る。

- **エラーログをテストの合否にする**: `Check.NoLogErrors()` / `Check.ExpectLogError(regex)`、または実行オプション `"fail_on_log_errors": true`（`--tests '{"fail_on_log_errors":true}'`、`harness.json` に書けば既定になる。ゲームの既知のノイズは `"ignore_log_errors": ["正規表現"]`）。テスト中に出たエラー/例外ログがあると、通っていたテストが `failed` になる。意図的にエラーを出すテストは `[HarnessTest(AllowLogErrors = true)]`。

Mod 側の `CLAUDE.md` には次のように書いておくとエージェントが使える:

```markdown
## 実機検証
ONI 実機で検証するには Gravitas.TestHarness を使う。テストは `[HarnessTest]` を付けた static メソッドとして書く（`#if DEBUG`）。
実行: `python <harness repo>/Gravitas.TestProbe/probe.py run --project . --tests '{"tags":["mymod"]}'`
ゲームを起動し本番の mods.json を書き換えるので、実行前にユーザーに確認する。
新しい Mod を初めて置くときは、DLL 配置 → `probe.py run --project . --setup`（ゲームを起動して Mod を mods.json に登録させ、閉じる）→ 通常の probe、の順にする。登録前に mods.json を書き換えるとゲームが起動中にクラッシュする（`<harness repo>/README.md` の「初回セットアップの順序」）。
詳細は `<harness repo>/README.md` と `protocol.md`。
```

## MCP サーバー（任意）

`Gravitas.TestProbe/mcp_server.py` は probe を MCP ツールとして公開する薄いラッパー（stdio、依存なし）。結果の JSON は CLI と同じ。

```
claude mcp add gravitas-probe -- python <このrepo>/Gravitas.TestProbe/mcp_server.py
```

| ツール | 内容 |
| --- | --- |
| `harness_run` | `probe.py run` と同じ（`project`、`save`、`tests`、`steps`、`keep`）。数分かかり、実機を起動して本番の `mods.json` を書き換える |
| `harness_http` | 起動中のゲームの API を直接呼ぶ（`keep: true` で残したゲーム向け） |
| `harness_fix_mods` | `mods.json` の確認・修復（ゲーム停止中のみ） |

CLI で足りる環境では不要。1 度に処理するのは 1 リクエストだけ（ゲームが 1 つしか起動できないため）。

## 仕組みと制約

- ゲーム起動は `Steam.exe -applaunch 457140 <引数>`（exe 直接起動は Steam の確認ダイアログで止まる）。設定は起動引数 `-harness-port/-harness-load/-harness-out` で渡す。
- 起動中に強制終了すると、次回が Mod セーフモード（全 Mod 無効）になる。probe は通常 `/quit` で正常終了し、強制終了した場合も、実行前の `mods.json` を書き戻すので、次回はセーフモードにならない。
- ロード後の「Mod 構成が変わった」ダイアログは抑制して `/status.suppressed_dialogs` に記録する。複製人間 0 人のセーブでもゲームオーバー画面で止まらないようにしてある（`-harness-allow-gameover` で解除）。
- 時間の単位はゲーム秒（1 サイクル = 600 s、1 ティック = 0.2 s）。3x 速度で 1 サイクルが実時間で約 200 秒かかる。
- API は 127.0.0.1 のみで待ち受ける。任意コードの評価は無く、`/invoke` はリフレクション呼び出しだけ。
