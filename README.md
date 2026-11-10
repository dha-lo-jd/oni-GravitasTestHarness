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
4. ゲームのモード管理画面でハーネスを一度有効化しておく（以後 probe が `mods.json` を毎回確認・修復する）。

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
検証対象の Mod を `probe.config.json` の `required_mods` に Mod ID で書くと、probe が `mods.json` でそれらを有効にしてから起動する。

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

テストは Mod 側に `#if DEBUG` で書く。ハーネスの DLL を参照すれば `Check` / `Wait` が使える
（csproj に `<Reference Include="Gravitas.TestHarness" Condition="'$(Configuration)'=='Debug'"><HintPath><このrepo>/Gravitas.TestHarness/bin/Debug/netstandard2.1/Gravitas.TestHarness.dll</HintPath><Private>false</Private></Reference>`）。
参照したくない場合は、`HarnessTestAttribute` と `HarnessAssertionException` を Mod 内に自前で定義すれば名前で認識される
（`CaffeineIncluded/CaffeineIncluded/HarnessTests.cs` が例。時間を進めるテスト（`Wait`）だけは参照が必要）。
`--tests` は、そのとき読み込まれている全テスト（ハーネスの自己テスト `harness.*` を含む）を実行する。自分のテストだけなら `--tests '{"tags":["mymod"]}'` のようにタグで絞る。

Mod 側の `CLAUDE.md` には次のように書いておくとエージェントが使える:

```markdown
## 実機検証
ONI 実機で検証するには Gravitas.TestHarness を使う。テストは `[HarnessTest]` を付けた static メソッドとして書く（`#if DEBUG`）。
実行: `python <harness repo>/Gravitas.TestProbe/probe.py run --project . --tests '{"tags":["mymod"]}'`
ゲームを起動し本番の mods.json を書き換えるので、実行前にユーザーに確認する。詳細は `<harness repo>/README.md` と `protocol.md`。
```

## 仕組みと制約

- ゲーム起動は `Steam.exe -applaunch 457140 <引数>`（exe 直接起動は Steam の確認ダイアログで止まる）。設定は起動引数 `-harness-port/-harness-load/-harness-out` で渡す。
- 起動中に強制終了すると、次回が Mod セーフモード（全 Mod 無効）になる。probe は通常 `/quit` で正常終了し、強制終了した場合は `mods.json` を修復する。
- ロード後の「Mod 構成が変わった」ダイアログは抑制して `/status.suppressed_dialogs` に記録する。複製人間 0 人のセーブでもゲームオーバー画面で止まらないようにしてある（`-harness-allow-gameover` で解除）。
- 時間の単位はゲーム秒（1 サイクル = 600 s、1 ティック = 0.2 s）。3x 速度で 1 サイクルが実時間で約 200 秒かかる。
- API は 127.0.0.1 のみで待ち受ける。任意コードの評価は無く、`/invoke` はリフレクション呼び出しだけ。
