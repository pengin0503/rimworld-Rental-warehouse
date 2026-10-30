# 信頼ランクと前払い交渉 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 信頼ランク（ポイント、ランク0～7、設定で編集できる表）、ランクによる依頼規模、帝国の依頼、前払い率の交渉、依頼到着の手紙通知を実装する。

**Architecture:** ロジックはゲーム非依存の `RentalWarehouse.Core` に置き、xUnitで固める。ゲーム側（`RentalWarehouse`）は、爵位・社会技能の読み取り、効果の適用、画面、手紙だけを担う薄いアダプターにする。台帳は schema 3 にまとめて上げる。

**Tech Stack:** C# (net10.0, Nullable enable, TreatWarningsAsErrors)、xUnit、RimWorld 1.7 参照アセンブリ（Krafs.Rimworld.Ref）、Harmony は新規利用なし。

**Spec:** `docs/superpowers/specs/2026-10-30-trust-rank-and-negotiation-design.md`（以下「設計書」。章番号は設計書の章）

## Global Constraints

- Core（`Source/RentalWarehouse.Core`）は `UnityEngine`・`Verse`・`RimWorld` を参照しない。ゲームの型は `Source/RentalWarehouse` にだけ置く。
- ランク表の初期値は設計書3.3のとおり: 必要ポイント `0,30,100,250,500,1000,2000,4000`、通常 `500–2000, 750–3000, 1000–4000, 1500–6000, 2000–8000, 3000–12000, 4000–16000, 5000–20000`、高危険度 `2000–6000, 3000–9000, 4000–12000, 6000–18000, 8000–24000, 12000–36000, 16000–48000, 20000–60000`、箱数最大 `4,4,5,6,7,8,9,10`。ポイント上限＝最上位の必要ポイント×1.5（6,000）。
- ポイント増減（設計書3.2）: 返還加点＝満期で返還した箱の価値÷100、損失減点＝紛失÷100×2＋横領÷100×4、中止減点＝早期返還または収納失敗でV÷100×0.5、`SystemCancelled` は0、結果は四捨五入（0.5は0から遠い側）。
- 降格の許容段数は通常1、帝国が依頼元なら2（設計書3.4）。帝国の最低ランクは2（`Limits.EmpireMinRank`）、最低爵位は defName `Knight`（序列300）、爵位の倍率＝`1 + (最高序列 − 300) / 1000`。
- 交渉の成功率＝`clamp(0.85 − 0.04×上げる幅(%) + 0.025×社会技能(0～20) + 0.03×ランク, 0.05, 0.95)`、乱数は `Mix(Terms.Seed, "negotiate")` の1回目の `NextDecimal()`、前払い率の上限は30%（設計書6章）。
- 箱数の検証上限は `Limits.HardMaxCrates = 12`。設定の入力範囲: 合計価値100銀以上、箱数1～12、警告は「合計価値の最大÷箱数の最大」が30,000銀超。
- 英語・日本語の翻訳キーとプレースホルダ（`{0}` など）は常に一致させる。ゲームのDLL・XMLをリポジトリにコミットしない（R2から取った資料は作業領域だけに置く）。
- 作業はこのブランチで1つのPRにまとめ、コミットはタスクごとに行う。段階（1～3）は設計書12章の順に実装する。

**検証コマンド**（Gameの変更を含むタスクは4つとも実行する）:

```bash
export PATH=$HOME/.dotnet:$PATH
dotnet test Tests/RentalWarehouse.Core.Tests -c Release
dotnet build Source/RentalWarehouse/RentalWarehouse.csproj -c Release
dotnet build Tests/RentalWarehouse.Game.Tests -c Release
python3 -B scripts/check_keys.py
```

`Mod/Defs` を変えたタスクは、さらに `dotnet run --project tools/DefValidator -c Release -- --assembly "$HOME/.nuget/packages/krafs.rimworld.ref/1.7.4985-unstable/ref/net10.0/Assembly-CSharp.dll" --assembly Mod/1.7/Assemblies/RentalWarehouse.dll --defs Mod/Defs` が `PASS` を出すことを確認する。

## Review Focus

仕様が暗に求めるが、各タスクの通常テストでは見落としやすい入力。それぞれ、担当タスクにテストを入れてある。

1. 妥当だが極端なランク表（最小＝最大、箱1個で巨額、箱に載らない価値）で、依頼生成が例外を出さず、最大値を超えない → Task 8。
2. ポイントの境界（0、上限、ランク0やランク1での降格の下限、帝国の2段）→ Task 3。
3. 古いセーブ・設定の欠落（ランク表なしのJSON、列の長さが違う、不正値）が初期値に戻り、移行が冪等 → Task 2・5・13。
4. 帝国の条件が依頼生成後に失われる（爵位の喪失、Royalty の無効化）。出ている帝国の依頼は受注できるまま、新しい帝国の依頼は出ない。帝国フラグが保存で保たれる → Task 9。
5. 交渉の境界（初回提示率30%、要求率＝初回、期限切れ、2回目、社会技能が範囲外）。依頼が0件の到着では手紙を出さない → Task 11・13。

---

## 段階1: 信頼ランク

### Task 1: 翻訳キー照合スクリプト

**Files:**
- Create: `scripts/check_keys.py`, `scripts/test_check_keys.py`
- Modify: `.github/workflows/validate.yml`（`Package validator self-test` の前に1ステップ）

**Interfaces:**
- Produces: `python3 -B scripts/check_keys.py` は、英日のキー集合と各キーの `{n}` プレースホルダの一致、およびソースの文字列リテラルのキーが英語側に定義されていることを確認し、問題があれば非0で終了する。定義名（`RW_ReturnPod`、`RW_VaultCompound`、`RW_VaultSite`、`RW_Warehouse`）と末尾が `_` のリテラル（動的に連結するキー）は未定義扱いにしない。
  関数: `parse_keyed(path) -> dict[str, str]`、`placeholder_diffs(en, ja) -> list[str]`、`undefined_keys(used, defined) -> list[str]`。

- [ ] **Step 1: 失敗するテストを書く（`scripts/test_check_keys.py`）**
  - `test_parse_keyed_reads_keys_and_values`、`test_placeholder_diffs_reports_a_missing_placeholder`（`{0}{1}` と `{0}` で1件）、`test_undefined_keys_ignores_def_names_and_trailing_underscore_prefixes`。
- [ ] **Step 2: 実行して失敗を確認する:** `python3 -B -m unittest discover -s scripts -p "test_check_keys.py"` → import error で FAIL。
- [ ] **Step 3: `scripts/check_keys.py` を実装する。** 解析は正規表現 `<(RW_\w+)>(.*?)</\1>`（DOTALL）、`main()` は両ファイルの差、プレースホルダ差、未定義キーを出力して終了コードを返す。
- [ ] **Step 4: テストが通り、現状のリポジトリで `python3 -B scripts/check_keys.py` が0で終わることを確認する。**
- [ ] **Step 5: CI に `python -B scripts/check_keys.py` を追加し、コミットする。** `.github/workflows` の変更をpushするには `workflows` 権限が要る。pushが権限で拒否された場合は、CIの変更だけをこのタスクのコミットから外して再pushし、利用者に「CIへの追加は手動で必要」と報告する。

### Task 2: ランク表・ポイント・schema 3（Core）

**Files:**
- Create: `Source/RentalWarehouse.Core/Trust.cs`, `Tests/RentalWarehouse.Core.Tests/TrustTests.cs`
- Modify: `Source/RentalWarehouse.Core/Settings.cs`（`WarehouseSettings.RankTable`、`Limits.HardMaxCrates = 12`・`Limits.EmpireMinRank = 2` を追加）、`Source/RentalWarehouse.Core/Model.cs`（`WorldState.TrustPoints`・`TrustAnnouncedRank`、`CurrentSchemaVersion = 3`）、`Source/RentalWarehouse.Core/Lifecycle.cs`（`Migration` に v2→v3）、`Tests/RentalWarehouse.Core.Tests/LifecycleTests.cs`（v1移行テストの期待版を `WorldState.CurrentSchemaVersion` に）

**Interfaces:**
- Produces（`Trust.cs`）:
  - `sealed class RankRow { int RequiredPoints; decimal NormalMin, NormalMax, HighRiskMin, HighRiskMax; int MaxCrates; RankRow Clone(); }`
  - `sealed class RankColumns { List<int> Points, NormalMin, NormalMax, HighRiskMin, HighRiskMax, MaxCrates; static RankColumns From(IReadOnlyList<RankRow>); List<RankRow>? ToRows(); }`（`ToRows` は列の長さが8でなければ `null`）
  - `static class TrustRanks`: `const int Count = 8`、`List<RankRow> Default()`、`int RankOf(IReadOnlyList<RankRow>, int points)`、`int PointCap(IReadOnlyList<RankRow>)`、`string? Validate(IReadOnlyList<RankRow>)`（理由キー: `RW_Rank_WrongCount`／`RW_Rank_FirstNotZero`／`RW_Rank_PointsOrder`／`RW_Rank_RangeInvalid`／`RW_Rank_CratesInvalid`。合計の最小は100以上、最小≤最大、箱数1～12）、`bool ExceedsPerCrateLimit(RankRow)`、`void ApplyMultiplier(RankRow, decimal)`（基準 通常500–2000／高危険度2000–6000 に倍率を掛けて四捨五入）、`decimal? MultiplierOf(RankRow)`（4つの合計が、ある倍率の結果に一致すればその倍率、でなければ `null`）、`bool SameTable(IReadOnlyList<RankRow>, IReadOnlyList<RankRow>)`
  - `WarehouseSettings.RankTable`: `List<RankRow>`（初期値 `TrustRanks.Default()`、`Clone()` は深いコピー）

- [ ] **Step 1: 失敗するテストを書く（`TrustTests.cs`）**
  - `Default_IsTheSpecTable`: 8行の `RequiredPoints` が `[0,30,100,250,500,1000,2000,4000]`、`MaxCrates` が `[4,4,5,6,7,8,9,10]`、行3が通常1500–6000／高危険度6000–18000、行7が通常5000–20000／高危険度20000–60000。
  - `RankOf_UsesTheHighestRowAtOrBelowThePoints`: `0→0, 29→0, 30→1, 99→1, 100→2, 3999→6, 4000→7, 999999→7`。
  - `PointCap_IsOneAndAHalfTimesTheTopThreshold`: 既定で6000、最上位を100にした表で150。
  - `Validate_*`: 既定は `null`。7行→`RW_Rank_WrongCount`、行0の必要ポイント1→`RW_Rank_FirstNotZero`、行3を250→100→`RW_Rank_PointsOrder`、`NormalMin=NormalMax+1`→`RW_Rank_RangeInvalid`、`NormalMin=99`→`RW_Rank_RangeInvalid`、`MaxCrates=0` と `13`→`RW_Rank_CratesInvalid`。
  - `ApplyMultiplier_ScalesFromTheBaseRanges`: 行に1.5で750/3000/3000/9000、3で1500/6000/6000/18000。`MultiplierOf_DefaultRows` が `[1,1.5,2,3,4,6,8,10]`、`NormalMax` を1増やした行は `null`。
  - `ExceedsPerCrateLimit`: 既定の全行は偽、`HighRiskMax=400000, MaxCrates=10` は真。
  - `RankColumns_RoundTripsTheDefaultTable`／`RankColumns_WithMismatchedLengths_ReturnsNull`／`SameTable_DetectsASingleChangedCell`。
  - `Clone_DeepCopiesTheRankTable`: 複製側の行を変えても元は変わらない。
  - `RankTable_SurvivesJsonRoundTrip`: カスタム表・`TrustPoints = 123`・`TrustAnnouncedRank = 2` を `StateCodec` で往復して一致。
  - `MissingRankTableInOldPayload_UsesTheDefault`（レビュー重点3）: `StateCodec.Import(new SavedState { SchemaVersion = 2, Payload = "{\"WorldSeed\":1}" })` の `Settings.RankTable` が8行で既定と同じ。
  - `Migration_V2_BecomesV3_WithZeroTrust`（`LifecycleTests.cs`）: v2のペイロードを `Migration.Upgrade` して `SchemaVersion == 3`、`TrustPoints == 0`、2回目の実行で同一のペイロード。
- [ ] **Step 2: 実行して失敗を確認する:** `dotnet test Tests/RentalWarehouse.Core.Tests -c Release --filter "FullyQualifiedName~TrustTests|FullyQualifiedName~Migration"` → コンパイルエラーで FAIL。
- [ ] **Step 3: 上記の型と項目を実装する。** `Migration.Upgrade` は `if (SchemaVersion == 1) FromV1` の次に `if (SchemaVersion == 2) FromV2` を続ける（`FromV2` は取り込んで `SchemaVersion = 3` で書き出すだけ。段階3で交渉の項目を足す）。`Default()` は毎回新しいリストを返す。
- [ ] **Step 4: 全Coreテストが通ることを確認する。**
- [ ] **Step 5: コミットする。**

### Task 3: 精算によるポイント増減と適用（Core）

**Files:**
- Modify: `Source/RentalWarehouse.Core/Trust.cs`（`TrustService`）、`Source/RentalWarehouse.Core/Model.cs`（`SettlementResult.TrustDelta`、`ContractTerms.DepositorIsEmpire`、`EffectKind.ApplyTrust`）、`Source/RentalWarehouse.Core/Accounting.cs`（`Settle` で `TrustDelta` を設定）、`Source/RentalWarehouse.Core/Contracts.cs`（`Finish` で効果を出す）
- Test: `Tests/RentalWarehouse.Core.Tests/TrustTests.cs`

**Interfaces:**
- Consumes: Task 2 の `TrustRanks`、`WarehouseSettings.RankTable`。
- Produces: `static class TrustService` — `int Points(WorldState)`（0～上限に丸めた値）、`int Rank(WorldState)`、`RankRow Row(WorldState)`、`int DeltaFor(ContractState)`、`(int Before, int After) Apply(WorldState, int delta, bool empire)`、`(int From, int To)? TakeRankChange(WorldState)`。`ApplyTrust` の効果は、`EffectId = <契約ID>:settle:trust`、`EntityId = 契約ID`、`Amount = TrustDelta`、`Payload["empire"] = "1"|"0"`。`TrustDelta == 0` なら効果を出さない。

- [ ] **Step 1: 失敗するテストを書く**（`Fixtures.Terms`／`Activate`／`RunUntil`／`ReturnAll` を使い、箱の価値は括弧内）
  - `Delta_FullyReturnedAtMaturity` (1箱1000) → `+10`。`Delta_OneLostOneReturned` (1000×2、片方を紛失) → `10 − 20 = −10`。`Delta_OneEmbezzledOneReturned` → `10 − 40 = −30`。
  - `Delta_EarlyReturnByPlayer` (1000) → `−5`。`Delta_StorageFailed` (1000、収納しない) → `−5`。
  - `Delta_DepositorHostileAllReturnedInWindow_IsZero`（`RequestEarlyReturn(c, ReturnReason.DepositorHostile, t)` 後に全返還）。`Delta_SystemCancelledIsZeroEvenWithLoss`（`SystemCancel` で箱を除去）。
  - `Delta_RoundsHalfAwayFromZero`: 箱250で満期返還→`+3`、箱500で早期返還→`−3`。
  - `Settlement_RecordsTheDeltaAndEmitsOneApplyTrustEffect`: `c.Settlement.TrustDelta == 10`、効果 `K1:settle:trust` が1件、`Amount == 10`、`Payload["empire"] == "0"`。デルタ0（`SystemCancelled`）では効果なし。`Terms.DepositorIsEmpire = true` で `Payload["empire"] == "1"`。
  - `Apply_DemotionIsLimitedToOneRankStep`（レビュー重点2）: 1000点（ランク5）・`−720` → `(1000, 500)`。帝国なら `(1000, 280)`。1000点・`−100` → 900。
  - `Apply_FloorsAtZeroAndAtTheCap`: 20点・`−50` → 0。30点（ランク1）・`−100`・帝国 → 0（許容段数2で、下のランクがない）。5990点・`+100` → 6000。
  - `Points_ClampsTheSavedValueToTheCurrentCap`: 保存値9999・上限6000 → `Points == 6000`。
  - `TakeRankChange_ReportsOnceThenNull`: `TrustAnnouncedRank = 0`・30点 → `(0, 1)`、直後は `null`。必要ポイントを `[1] = 10` に編集して20点なら `(0, 1)`（設定変更でも検知）。
- [ ] **Step 2: 実行して失敗を確認する（コンパイルエラー）。**
- [ ] **Step 3: 実装する。** `DeltaFor` は設計書3.2の式（`Math.Round(x, 0, MidpointRounding.AwayFromZero)`）。`Apply` は、精算前のランクから `許容段数` を引いたランクの必要ポイントを下限にして、減点のときだけ `max(新, 下限)` を取る。`Accounting.Settle` は `result.TrustDelta = TrustService.DeltaFor(state)`、`ContractEngine.Finish` は既存の友好度の効果の隣に `ApplyTrust` を追加する。
- [ ] **Step 4: 全Coreテストが通ることを確認する。**
- [ ] **Step 5: コミットする。**

### Task 4: ゲーム側の適用とランク変化の通知

**Files:**
- Create: `Source/RentalWarehouse/Integration/TrustAdapter.cs`, `Tests/RentalWarehouse.Game.Tests/TrustCases.cs`
- Modify: `Source/RentalWarehouse/Integration/EffectDispatcher.cs`（`[EffectKind.ApplyTrust] = ApplyTrust`）、`Source/RentalWarehouse/Integration/WarehouseWorldComponent.cs`（`EffectDispatcher.ProcessQueue(this)` の後に `TrustAdapter.Tick(this)`）、`Mod/Languages/English|Japanese/Keyed/RentalWarehouse.xml`

**Interfaces:**
- Consumes: Task 3 の `TrustService.Apply`／`TakeRankChange`。
- Produces: `static class TrustAdapter { static void Tick(WarehouseWorldComponent world); }`（ランクが変わっていればメッセージを出す）。翻訳キー: `RW_TrustRank_0`～`RW_TrustRank_7`（新参、登録業者、信用業者、認定倉庫主、名士、大倉庫主、倉庫貴族、倉庫王／Newcomer、Registered Trader、Trusted Trader、Certified Keeper、Notable、Grand Keeper、Warehouse Noble、Warehouse King）、`RW_Msg_RankUp`・`RW_Msg_RankDown`（`{0}`＝ランク名）。

- [ ] **Step 1: ゲーム内テストを書く（`TrustCases.cs`。`DebugAction` で実行。実行は利用者の環境）**
  - `ApplyTrustEffectUpdatesPointsOnce`: `ApplyTrust` を2回同じIDで積み、`TrustPoints` が一度だけ変わる。`RankChangeIsAnnouncedOnce`: ポイントを閾値越えにして `TrustAdapter.Tick` を2回呼んでも、`TrustAnnouncedRank` が更新され2回目は何も出ない。
- [ ] **Step 2: 実装する。** `ApplyTrust` は `Payload["empire"] == "1"` と `(int)Amount` で `TrustService.Apply` を呼び、`true` を返す。`TrustAdapter.Tick` は `TakeRankChange` が値を返したとき、昇格なら `MessageTypeDefOf.PositiveEvent`、降格なら `NegativeEvent` でメッセージを出す。
- [ ] **Step 3: 翻訳を英日で追加する。**
- [ ] **Step 4: 検証コマンド4つがすべて成功することを確認する。**
- [ ] **Step 5: コミットする。**

### Task 5: MOD設定の保存と台帳への同期

**Files:**
- Modify: `Source/RentalWarehouse/UI/SettingsView.cs`（`RentalWarehouseSettings`）、`Tests/RentalWarehouse.Core.Tests/TrustTests.cs`（`RankColumns` は Task 2 で作成済み。ここでは不正列の扱いを追加）

**Interfaces:**
- Consumes: `RankColumns`、`TrustRanks.Validate`／`SameTable`。
- Produces: `RentalWarehouseSettings` に `List<int> rankPoints, rankNormalMin, rankNormalMax, rankHighMin, rankHighMax, rankCrates`（`Scribe_Collections.Look(..., LookMode.Value)` で保存）、`List<RankRow> RankRows()`（列が不正または `Validate` が非 `null` なら既定表を返し `Log.Warning` を1回出す）、`void SetRankRows(IReadOnlyList<RankRow>)`、`void ResetRanks()`。`ToCore()` は `RankTable = RankRows()` を含み、`SyncInto` は `TrustRanks.SameTable` で比較して変化があれば `SettingsVersion++`。

- [ ] **Step 1: Core のテストを追加する（レビュー重点3）:** `RankColumns_WithABadValue_FailsValidationAndTheCallerFallsBack`（`Points` の1つを負数にした列を `ToRows` → `Validate` が非 `null`）。`RankColumns_ShortColumn_ReturnsNull`（先のテストと別に、各列それぞれ7要素のケースを `Theory` で）。
- [ ] **Step 2: 実行して通ることを確認する（Task 2 の実装で通る）。通らなければ `ToRows` を直す。**
- [ ] **Step 3: `RentalWarehouseSettings` に列と `ExposeData` を実装する。** 読み込み後の列が空（旧設定ファイル）の場合は既定表を入れる。
- [ ] **Step 4: 検証コマンド4つがすべて成功することを確認する。**
- [ ] **Step 5: コミットする。**

### Task 6: ランク表エディタ画面

**Files:**
- Create: `Source/RentalWarehouse/UI/RankTableDialog.cs`
- Modify: `Source/RentalWarehouse/UI/SettingsView.cs`（`SettingsView.Draw` に「ランク表を編集」ボタン、「初期値に戻す」でランク表も戻す）、`Mod/Languages/English|Japanese/Keyed/RentalWarehouse.xml`

**Interfaces:**
- Consumes: Task 2 の `TrustRanks.ApplyMultiplier`／`MultiplierOf`／`Validate`／`ExceedsPerCrateLimit`、Task 5 の `RankRows()`／`SetRankRows`／`ResetRanks`。
- Produces: `class RankTableDialog : Window { RankTableDialog(RentalWarehouseSettings s); }`。翻訳キー: `RW_Set_EditRanks`、`RW_Rank_Col_Points`／`_Mult`／`_NormalMin`／`_NormalMax`／`_HighMin`／`_HighMax`／`_Crates`、`RW_Rank_Custom`（「個別設定」）、`RW_Rank_Reset`、`RW_Rank_Warn`（`{0}`＝ランク名）、`RW_Rank_Err_*`（Task 2 の理由キーに対応する文言）。

- [ ] **Step 1: ダイアログを実装する。** 8行×7列のスクロールする表。数値欄は `Widgets.TextFieldNumeric` を使い、編集中のコピー（`List<RankRow>`）を持つ。倍率欄に入力すると `ApplyMultiplier` を行に適用し、合計を直接編集した行は `MultiplierOf` が `null` なので「個別設定」と表示する。ランク0の必要ポイントは編集不可（0固定）。`Validate` が非 `null` の間は理由を赤字で出し、`SetRankRows` へは反映しない。`ExceedsPerCrateLimit` の行には警告を出す（保存は妨げない）。「初期値に戻す」で `Default()` を反映する。閉じるときに `RentalWarehouseMod.Settings.Write()` を呼ぶ。
- [ ] **Step 2: 英日の翻訳を追加する。**
- [ ] **Step 3: 検証コマンド4つがすべて成功することを確認する。**
- [ ] **Step 4: コミットする。**

### Task 7: ランク表示・精算表示・段階1の文書と確認

**Files:**
- Create: `Source/RentalWarehouse/UI/TrustView.cs`
- Modify: `Source/RentalWarehouse/UI/OfferView.cs`（上部の説明を2行にし、高さを64に）、`Source/RentalWarehouse/UI/AccountingView.cs`（`Settlement` に信頼の増減）、翻訳XML、`README.md`、`docs/testing/acceptance.md`、`docs/superpowers/specs/2026-10-23-rental-warehouse-design.md`（「初期版対象外」の「信用ランク」に「[2026-10-30の設計書](2026-10-30-trust-rank-and-negotiation-design.md)で対象化」と追記）

**Interfaces:**
- Consumes: `TrustService`、`TrustRanks`。
- Produces: `static class TrustView { static string Summary(WorldState state); }`。翻訳キー: `RW_TrustSummary`（「信頼ランク: {0}（{1}点 / 次のランクまで{2}点）」）、`RW_TrustSummaryMax`（「信頼ランク: {0}（{1}点・最高ランク）」）、`RW_TrustLine`（「信頼 {0}」。`{0}` は `+0;-0;0` 書式）。

- [ ] **Step 1: `TrustView.Summary` を実装する**（最高ランクなら `RW_TrustSummaryMax`）。`OfferView.Draw` の上部ラベルに `LoadSummary` の次の行として表示する。`AccountingView.Settlement` は既存の行の末尾に `" " + "RW_TrustLine".Translate(s.TrustDelta.ToString("+0;-0;0"))` を足す。
- [ ] **Step 2: 文書を更新する。** README の経営の説明に信頼ランクを1段落、`acceptance.md` にケース A17（精算でポイントが増減し、降格が1段に限られる）、A18（設定のランク表を編集して新しい依頼に反映される）を「期待結果（未検証）」として追加。
- [ ] **Step 3: 検証コマンド4つがすべて成功することを確認する。**
- [ ] **Step 4: コミットする。段階1の確認として、全コマンドの出力（Coreテスト件数、ビルド、キー照合）を記録する。**

---

## 段階2: 依頼の規模と帝国、通知

### Task 8: ランクによる依頼の規模（Core）

**Files:**
- Modify: `Source/RentalWarehouse.Core/Offers.cs`（`MakeOffer` がランクの行を使う）、`Source/RentalWarehouse.Core/Contracts.cs`（`ValidTerms` の箱数上限を `Limits.HardMaxCrates` に）、`Source/RentalWarehouse.Core/Settings.cs`（`Limits.MaxCrates` を削除）、`Tests/RentalWarehouse.Core.Tests/OfferTests.cs`（`Context`／`Goods`／`OpenWorld` を `internal static` にし、`Limits.MaxCrates` の参照を `Limits.HardMaxCrates` に）
- Create: `Tests/RentalWarehouse.Core.Tests/OfferScalingTests.cs`

**Interfaces:**
- Consumes: `TrustService.Row(WorldState)`。
- Produces: 通常は `row.NormalMin..NormalMax`、高危険度は `row.HighRiskMin..HighRiskMax`、箱数は `rng.Range(1, row.MaxCrates)` から `row.MaxCrates` まで探す。合計の目標は `rng.Range(Math.Min(min / MinFillRatio, max), max)`（最小÷0.9が最大を超える行でも最大を超えない）。

- [ ] **Step 1: 失敗するテストを書く（`OfferScalingTests.cs`。`OfferTests` の `Context(withPursuer: true)` を使う）**
  - `EveryRank_OffersStayInsideThatRanksRangesAndCrateCap` (`Theory`、ランク0～7): `world.TrustPoints = 既定表の RequiredPoints[ランク]`、90日回す。全依頼で `TotalValue` が種類別の最小以上最大以下、`CargoIds.Count <= row.MaxCrates`、依頼が1件以上。
  - `RankSeven_OffersMoreThanFourCrates`: ランク7で180日、`Max(CargoIds.Count) > 4`。
  - `RankChange_AppliesToNewBatchesOnly`: ランク0で最初の依頼を作り、`TrustPoints = 4000` にしても既存の依頼の条件は変わらず、その後の依頼で通常の `TotalValue > 2000` が現れる。
  - `RowWithMinEqualToMax_NeverExceedsMax`（レビュー重点1）: 行0を `NormalMin=NormalMax=1000`、`HighRiskMin=HighRiskMax=3000`、`MaxCrates=1` に編集して60日回すと、全依頼で `TotalValue <= 最大` かつ `>= 最大×0.9`、例外なし。
  - `RowWhoseCratesCannotBeBuilt_ProducesNoOfferAndNoException`（同）: 行0の4つの合計を200,000、`MaxCrates=1` にすると、60日で依頼が0件で例外なし（箱1個の積載は33kgまで）。
- [ ] **Step 2: 実行して失敗を確認する。**
- [ ] **Step 3: `MakeOffer` を実装する。** `OfferService.Generate` が `TrustService.Row(world)` を取って `MakeBatch` → `MakeOffer` に渡す。乱数の消費順は既存と同じにする（`crates` の範囲だけが変わる）。
- [ ] **Step 4: 全Coreテストが通ることを確認する。**
- [ ] **Step 5: コミットする。**

### Task 9: 帝国の依頼（Core）

**Files:**
- Modify: `Source/RentalWarehouse.Core/Offers.cs`（`FactionInfo.IsEmpire`、`OfferContext.EmpireTitleFactor`・`EmpireGoods`、`Depositors`、`MakeOffer`）、`Source/RentalWarehouse.Core/Trust.cs`（`EmpireTitleFactor`）
- Create: `Tests/RentalWarehouse.Core.Tests/EmpireTests.cs`

**Interfaces:**
- Produces: `FactionInfo.IsEmpire: bool`、`OfferContext.EmpireTitleFactor: decimal`（0＝資格なし）、`OfferContext.EmpireGoods: IList<Good>`、`TrustRanks.EmpireTitleFactor(int? highestSeniority, int knightSeniority) -> decimal`（最高序列が `null` または `knightSeniority` 未満なら0、そうでなければ `1 + (最高序列 − knightSeniority) / 1000m`）。帝国が預け主の候補に入る条件は「`IsEmpire` かつ `EmpireTitleFactor > 0` かつ `EmpireGoods` が空でない かつ ランク ≥ `Limits.EmpireMinRank`」。帝国の依頼は `EmpireGoods` だけから箱を作り、合計価値の最小・最大に `EmpireTitleFactor` を掛け、`terms.DepositorIsEmpire = true` にする。

- [ ] **Step 1: 失敗するテストを書く（`EmpireTests.cs`。帝国は `FactionId = "Faction_E"`、`HostileTo` に `Faction_2` の敵対を設定）**
  - `EmpireTitleFactor_StartsAtKnight`: `(null,300)=0, (200,300)=0, (300,300)=1.0, (400,300)=1.1, (600,300)=1.3`。
  - `Empire_NeverOffers_BelowRank2`: 99点（ランク1）で120日回しても `DepositorIsEmpire` の依頼なし。100点（ランク2）で1件以上。
  - `Empire_NeverOffers_WithoutAFactorOrACatalogue`: 係数0、または `EmpireGoods` が空だと帝国の依頼なし。
  - `EmpireOffers_UseOnlyTheEmpireCatalogue_AndScaleWithTheFactor`: 係数1.3・ランク2で、帝国の依頼の中身の `ThingDefName` が `EmpireGoods`（テストでは `MeleeWeapon_MonoSword` 2000銀/2kg、`MeleeWeapon_PlasmaSwordBladelink` 3000銀/2kg、`Apparel_PsyfocusRobe` 600銀/0.5kg、`BroadshieldCore` 350銀/2kg）の範囲内、`DepositorFactionId == "Faction_E"`、合計が通常なら 1000×1.3 以上 4000×1.3 以下（高危険度は 4000×1.3 以上 12000×1.3 以下）。帝国以外の依頼は `DepositorIsEmpire == false` で、`EmpireGoods` の品を含まない。
  - `EmpireOffersAlreadyMade_SurviveLosingEligibility`（レビュー重点4）: 帝国の依頼を作った後、係数を0にしてポイントを0にしても、その依頼は `world.Offers` に残り `ContractEngine.Accept` が成功する。以後の依頼には帝国が出ない。
  - `DepositorIsEmpire_SurvivesPersistence`: `StateCodec` の往復で `Terms.DepositorIsEmpire` が保たれる。
- [ ] **Step 2: 実行して失敗を確認する。**
- [ ] **Step 3: 実装する。** `Depositors` が帝国を条件で除外し、`MakeOffer` が預け主ごとに品目群を切り替える。高危険度は既存どおり追跡者がいる場合だけ。
- [ ] **Step 4: 全Coreテストが通ることを確認する。**
- [ ] **Step 5: コミットする。**

### Task 10: 帝国の爵位・カタログの読み取り（ゲーム）

**Files:**
- Create: `Source/RentalWarehouse/Integration/EmpireAdapter.cs`
- Modify: `Source/RentalWarehouse/Quests/OfferGenerator.cs`（既存のカタログからの `Good` 生成を `MakeGood(defName, stuffName)` に切り出し、`BuildContext` が `IsEmpire`・`EmpireTitleFactor`・`EmpireGoods` を設定）、`Mod/About/About.xml`（`loadAfter` に `ludeon.rimworld.royalty` を追加。依存ではなく読み込み順だけ）

**Interfaces:**
- Produces: `static class EmpireAdapter` — `bool RoyaltyActive`、`Faction EmpireFaction()`（defName `Empire`。Royalty が無効または派閥が無ければ `null`）、`int? HighestTitleSeniority()`（`PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists` の各 `pawn.royalty?.GetCurrentTitle(EmpireFaction())?.seniority` の最大）、`int KnightSeniority()`（`DefDatabase<RoyalTitleDef>.GetNamedSilentFail("Knight")` の `seniority`、無ければ `-1`）、`IList<Good> Catalogue()`（設計書5.3の defName を `GetNamedSilentFail` で確認し、存在する品だけ `MakeGood` で作る）、`string Hint(WarehouseWorldComponent)`（Royalty が有効で帝国が候補に入っていないとき、不足条件を1行で返す。条件を満たしていれば `null`）。

- [ ] **Step 1: 実コードで次を確認してから実装する（設計書13章）。** `Pawn_RoyaltyTracker.GetCurrentTitle(Faction)` の戻り値と `RoyalTitleDef.seniority` の型、`ModsConfig.RoyaltyActive`、`GetStatValueAbstract(StatDefOf.MarketValue/Mass)` が帝国のカタログの品で正の値を返すこと（`/tmp/claude-0/inspect` の `Inspect` ツールで型・メンバー名を確認し、挙動に疑いがあれば `ilspycmd` で読む）。確認結果とずれる点は、このタスクの実装で吸収し、設計書に反映する。
- [ ] **Step 2: 実装する。** `EmpireTitleFactor` は Core の `TrustRanks.EmpireTitleFactor(HighestTitleSeniority(), KnightSeniority())`。`KnightSeniority() < 0` のときは係数0にして、`EmpireGoods` も空にする。
- [ ] **Step 3: 検証コマンド4つに加え、`DefValidator` が `PASS` で、`About.xml` を検査する `python3 -B -m unittest discover -s scripts -p "test_*.py"` が通ることを確認する。**
- [ ] **Step 4: コミットする。**

### Task 11: 到着の手紙と帝国の案内

**Files:**
- Create: `Source/RentalWarehouse/UI/OfferNotice.cs`, `Source/RentalWarehouse/UI/ChoiceLetter_Offers.cs`, `Mod/Defs/Letters.xml`
- Modify: `Source/RentalWarehouse/Quests/OfferGenerator.cs`（`Tick` のメッセージを手紙に置き換え）、`Source/RentalWarehouse/UI/OfferView.cs`（案内の1行）、翻訳XML（`RW_Msg_NewOffers` を削除し、手紙のキーを追加）、`Tests/RentalWarehouse.Game.Tests/EmpireCases.cs`

**Interfaces:**
- Produces: `static class OfferNotice { static void Send(WarehouseWorldComponent world, IReadOnlyList<ContractTerms> created); }`（`created.Count == 0` なら何もしない）。`ChoiceLetter_Offers : ChoiceLetter`（`Choices` を上書きし、「貸倉庫を開く」（`WarehouseTab.Open()`）と閉じるの2つを返す。`Verse.ChoiceLetter.Choices` は `virtual` で確認済み）。`Letters.xml` に `LetterDef`（`defName` `RW_OffersLetter`、`letterClass` `RentalWarehouse.ChoiceLetter_Offers`）。翻訳キー: `RW_Letter_NewOffersTitle`、`RW_Letter_OfferLine`（`{0}`依頼元、`{1}`種類、`{2}`箱数、`{3}`合計価値、`{4}`期間（日）、`{5}`前払い率%、`{6}`期限）、`RW_Letter_EmpireMark`（「帝国」）、`RW_Letter_OpenTab`、`RW_Hint_Empire`（「帝国の依頼にはランク{0}以上と、{1}以上の爵位を持つコロニストが必要です」）。

- [ ] **Step 1: ゲーム内テストを書く（`EmpireCases.cs`。実行は利用者の環境）:** `NoLetterForAnEmptyBatch`（`OfferNotice.Send` に空のリストを渡しても `Find.LetterStack.LettersListForReading.Count` が変わらない）、`EmpireDisabledWithoutRoyalty`（Royalty が無効な環境では `EmpireAdapter.EmpireFaction() == null` で `BuildContext` の `EmpireTitleFactor == 0`）。
- [ ] **Step 2: `OfferNotice` と `ChoiceLetter_Offers` を実装する。** 本文は依頼ごとに `RW_Letter_OfferLine` を並べ、帝国の依頼には `RW_Letter_EmpireMark` を付ける。`Find.LetterStack.ReceiveLetter(letter)` で送る。
- [ ] **Step 3: `OfferGenerator.Tick` を `OfferNotice.Send(world, created)` に置き換え、`OfferView` に `EmpireAdapter.Hint` の結果を1行（非 `null` のとき）表示する。**
- [ ] **Step 4: 検証コマンド4つ（`DefValidator` を含む）がすべて成功することを確認する。**
- [ ] **Step 5: コミットする。**

### Task 12: 段階2の文書と確認

**Files:**
- Modify: `README.md`、`docs/testing/acceptance.md`

- [ ] **Step 1: README に、ランク別の依頼規模、帝国の依頼の条件、到着の手紙を追記する。**
- [ ] **Step 2: `acceptance.md` にケース A19（ランク別の規模と箱数）、A20（Royalty で帝国の依頼が条件どおりに出る・出ない）、A21（依頼到着の手紙と「貸倉庫を開く」）を「未検証」で追加する。**
- [ ] **Step 3: 検証コマンドを実行し、結果を記録してコミットする。**

---

## 段階3: 前払い交渉

### Task 13: 交渉（Core）と移行

**Files:**
- Create: `Source/RentalWarehouse.Core/Negotiation.cs`, `Tests/RentalWarehouse.Core.Tests/NegotiationTests.cs`
- Modify: `Source/RentalWarehouse.Core/Model.cs`（`ContractTerms.OpeningPrepaymentRate`、`ContractState.Negotiation`、`enum NegotiationState { None, Succeeded, Failed }`）、`Source/RentalWarehouse.Core/Contracts.cs`（`ContractTermsFactory.Create` が `OpeningPrepaymentRate = prepaymentRate` を設定、`ValidTerms` が `PrepaymentRate` を0～0.30に限る）、`Source/RentalWarehouse.Core/Lifecycle.cs`（`FromV2` で提示中の依頼の `OpeningPrepaymentRate` を現在の率にする）

**Interfaces:**
- Produces: `static class NegotiationService` — `const int MaxPercent = 30`、`const int MaxSocial = 20`、`decimal SuccessChance(int openingPercent, int requestedPercent, int socialLevel, int rank)`（社会技能は0～20に丸める）、`decimal Roll(int offerSeed)`、`Decision<ContractState> Attempt(WorldState world, string contractId, int requestedPercent, int socialLevel, long now)`。`Success == true` は「交渉が行われた」意味で、結果は `NewState.Negotiation`（`Succeeded`／`Failed`）で読む。拒否の理由キー: `RW_Reject_NotOffered`、`RW_Reject_OfferExpired`、`RW_Reject_AlreadyNegotiated`、`RW_Reject_InvalidRate`（要求率が初回提示率以下、または30超）。成功時は `offer.Terms.PrepaymentRate = requestedPercent / 100m`。

- [ ] **Step 1: 失敗するテストを書く（`NegotiationTests.cs`。初回提示率10%の依頼は `Terms(..., prepay: 0.10m)`。乱数の都合で、`Roll(seed) < 0.05` と `Roll(seed) >= 0.95` になる `Seed` を1～100000の探索で見つけるヘルパーを使う）**
  - `SuccessChance_MatchesTheSpecTable`: `(10,15,5,0)=0.775`、`(10,20,10,2)=0.76`、`(10,30,10,2)=0.36`、`(5,20,2,0)=0.30`、`(0,30,15,5)=0.175`。
  - `SuccessChance_ClampsToFiveAndNinetyFivePercent`: `(0,30,0,0)=0.05`、`(29,30,20,7)=0.95`。`SuccessChance_ClampsSocialSkillToTwenty`: `(10,11,40,0)` は `(10,11,20,0)` と等しい。
  - `Attempt_RollBelowChance_Succeeds`: 成功し、`Terms.PrepaymentRate == 0.15m`、`Negotiation == Succeeded`、`NominalTotalReward` は不変、`Accounting.Prepayment(terms)` が新しい率で再計算される。`Attempt_RollAtOrAboveChance_Fails`: `PrepaymentRate == 0.10m` のまま、`Negotiation == Failed`。
  - `Attempt_Twice_RejectsTheSecond`: 2回目は `RW_Reject_AlreadyNegotiated`、率は不変。
  - `Attempt_RejectsInvalidRatesAndExpiredOffers`（レビュー重点5）: 要求率10（初回と同じ）と9と31は `RW_Reject_InvalidRate`、初回提示率30%の依頼は何を要求しても `RW_Reject_InvalidRate`、`now >= OfferExpiresTick` は `RW_Reject_OfferExpired`、受注済み・存在しない契約IDは `RW_Reject_NotOffered`。
  - `Attempt_IsDeterministicAcrossSaveAndLoad`: 保存・読込を挟んだ後の同じ条件の結果が一致する。
  - `NegotiatedRate_FlowsIntoTheAcceptedContract`: 成功後に `Activate` すると `Receivable == floor(FullTermFee × 新しい率)`。
  - `Migration_V2_OfferedTermsBecomeNegotiable`: v2ペイロード（`OpeningPrepaymentRate = 0`、`PrepaymentRate = 0.2`）の提示中の依頼が、移行後に `OpeningPrepaymentRate == 0.2` で `Negotiation == None`。移行は冪等。
- [ ] **Step 2: 実行して失敗を確認する。**
- [ ] **Step 3: 実装する。** `Attempt` は提示中の依頼（`world.Offers` で `Phase == Offered`）だけを対象にし、`TrustService.Rank(world)` を成功率に使う。
- [ ] **Step 4: 全Coreテストが通ることを確認する。**
- [ ] **Step 5: コミットする。**

### Task 14: 交渉の画面（ゲーム）

**Files:**
- Create: `Source/RentalWarehouse/Integration/NegotiatorAdapter.cs`, `Source/RentalWarehouse/UI/NegotiationDialog.cs`, `Tests/RentalWarehouse.Game.Tests/NegotiationCases.cs`
- Modify: `Source/RentalWarehouse/UI/OfferView.cs`（「交渉」ボタンと前払いの行）、翻訳XML

**Interfaces:**
- Consumes: Task 13 の `NegotiationService`。
- Produces: `static class NegotiatorAdapter { static List<Negotiator> Candidates(); }`（`sealed class Negotiator { Pawn Pawn; int Social; }`。自由なコロニストで、プレイヤーの拠点マップに出現していて、`!pawn.Downed`、`!pawn.WorkTagIsDisabled(WorkTags.Social)`、会話ができる者。社会技能は `pawn.skills.GetSkill(SkillDefOf.Social).Level`）。`NegotiationDialog(ContractState offer) : Window`。翻訳キー: `RW_Negotiate`、`RW_NegotiateTitle`、`RW_NegotiateChance`（`{0}`＝%）、`RW_NegotiateGo`、`RW_Msg_NegotiationSucceeded`／`RW_Msg_NegotiationFailed`（`{0}`契約ID、`{1}`率%）、`RW_PrepaymentLineNegotiated`（「前払い: 初回{0}% → 現在{1}%（{2}）…」）、`RW_Negotiation_None`／`_Succeeded`／`_Failed`。

- [ ] **Step 1: ゲーム内テストを書く（`NegotiationCases.cs`）:** `CandidatesAreFreeColonistsOnHomeMaps`（一覧が空でなく、全員が自由なコロニストでマップに出現している）、`AttemptAppliesThroughTheWorldComponent`（`NegotiationService.Attempt` の結果が台帳の依頼に反映され、2回目は拒否される）。
- [ ] **Step 2: 実装する。** `OfferView.DrawOffer` に、`offer.Negotiation == None` かつ `OpeningPrepaymentRate < 0.30m` のときだけ有効な「交渉」ボタンを足す。ダイアログは、交渉役の一覧（名前と社会技能）、要求率のスライダー（初回提示率+1%～30%）、選択に応じた `SuccessChance` の表示、実行ボタンを持ち、実行後に結果のメッセージを出して閉じる。`OfferView.Describe` の前払いの行は、交渉済みなら `RW_PrepaymentLineNegotiated` を使う。
- [ ] **Step 3: 検証コマンド4つがすべて成功することを確認する。**
- [ ] **Step 4: コミットする。**

### Task 15: 段階3の文書、最終確認、PR

**Files:**
- Modify: `README.md`、`docs/testing/acceptance.md`、`docs/superpowers/specs/2026-10-30-trust-rank-and-negotiation-design.md`（状態を「実装済み（ゲーム動作は未検証）」に）

- [ ] **Step 1: README に交渉の説明、`acceptance.md` にケース A22（交渉の成功・失敗・再交渉不可）と A23（セーブ・ロードで結果が変わらない）を「未検証」で追加し、設計書の状態を更新する。**
- [ ] **Step 2: 最終確認として、検証コマンド4つに加え、`dotnet test tools/DefValidator.Tests -c Release`、`DefValidator` の実行、`python3 -B -m unittest discover -s scripts -p "test_*.py"` を実行し、すべての結果（Coreテスト件数を含む）を記録する。**
- [ ] **Step 3: 変更をpushし、PRを作る。** 説明は段階1～3に分け、各段階で確認できたこと（Coreテスト、ビルド、DefValidator、キー照合）と、確認できていないこと（ゲーム内の画面・手紙・爵位の読み取り・交渉ダイアログ、パッケージ作成）を分けて書く。CIが通るまで監視する。
