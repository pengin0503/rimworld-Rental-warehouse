# Rental Warehouse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 逐次実行を基本とし、サブエージェントは使わない。2026-10-23のユーザー指示で実行済み。

**Goal:** 貸倉庫経営、貨物防衛、他派閥の独立金庫襲撃をRimWorld 1.7で成立させる。

**Architecture:** 契約・精算・襲撃予定・在庫はゲーム非依存のCore。Thing、Quest、Job、Site、UI、保存はゲームアダプター。所有権と一意の副作用IDを共有し二重生成を防ぐ。

**Tech Stack:** C#、XML、ローカルゲームDLL。Coreはnetstandard2.0/C#7.3、テストはnet8.0/xUnitを初期案とする。ゲーム側TFM、SDK、依存の正確な版はT0で確認・固定する。T0結果: 1.7.4985はnet10.0（Waylith/.NET 10）。Core/Game/Testsともnet10.0、参照はKrafs.Rimworld.Ref 1.7.4985-unstable、HarmonyはLib.Harmony.Ref 2.3.1（[API監査](../../technical/1.7-api-audit.md)）。

**Spec:** [仕様書](../specs/2026-10-23-rental-warehouse-design.md)

**Acceptance:** [受入テスト計画](../../testing/acceptance.md)

## Global Constraints

- 対象はRimWorld 1.7。正確なゲームビルド番号を実装時に固定し、更新時に再検証する。
- 必須DLCなし。外部依存はAPI調査で必要性を確認して最小限にする。
- 既存セーブへ追加できることを目標とする。MOD途中削除は非対応と明記する。
- ゲームDLLをリポジトリ・CI成果物・配布ZIPへ含めない。
- 全MOD互換性を保証しない。実際に検証した組合せを記載する。
- （仕様・計画段階の制約）Markdown文書だけを作成し、ソース・XML・DLL・配布ZIPは作成しない。2026-10-23のユーザー指示で解除し、実装した。
- 他派閥施設はA方式のみ。日英対応。数値の正本は仕様書、変更時は期待値も同時更新。
- 各タスクは関連ローカルテストのみ。全体検証は最後に行い、CIは最終確認で一度を基本とする。

## Review Focus

1. 支払/箱生成/外交の途中で保存・例外: T3/T5/T9、A11。
2. 施設/派閥/地図/Def消失: T9、A12/A13。
3. 一部略奪後の再訪・閉鎖後の開封: T7/T8、A09。
4. 複数契約と通常襲撃の競合: T6、A05。
5. マップ上にない残高と未開封箱の資産計上: T2/T5/T8、A10。

## 順序と到達点

T0→T1→T2→T3→T4→T5→T6→T7→T8→T9→T10。
T5で経営、T6で防衛、T8で遠征が成立する。途中成果を初期版完成と呼ばず、T10まで検証する。

本計画のパスはすべて将来の作成予定であり、現在の実装ではない。以下の略記はファイルパスの共通接頭辞とする。

| 略記 | 接頭辞 | 責任 |
|---|---|---|
| Core/ | Source/RentalWarehouse.Core/ | 契約・料金・貨物・予定の純ロジック |
| Game/ | Source/RentalWarehouse/ | ゲームとの接続 |
| Tests/ | Tests/RentalWarehouse.Core.Tests/ | ゲームDLL不要の自動テスト |
| GameTests/ | Tests/RentalWarehouse.Game.Tests/ | ローカル実ゲームの結合試験 |

## 共通データと境界（T1/T3で定義）

IDはstring、絶対tickはlong、金額・比率はdecimal、seedはint。ゲームAPIの型をCoreへ持ち込まない。DTOは保存可能、入力不正は理由キー付き拒否とする。

- ContractTerms: ContractId, DepositorFactionId, Kind, CargoIds, CargoValues, RequiredStorage, DurationTicks, DailyBase, DailyValueRate, CompletionRate, ScheduledRaidCount, NominalTotalReward, Seed, SettingsVersion。
- ContractState: Terms, Phase, MapId, AcceptedTick, DeliveryTick, ActiveTick?, EndTick?, ReturnDeadlineTick?, CargoStates, AccruedFee, PostedFee, ClaimedFee, StartedRaids, CancelReason?。
- CargoRecord: CargoId, ContractId?, SiteId?, OwnerFactionId, Custody, Manifest, DeclaredValue, MassKg, HitPoints, RequiredStorage, ContainerId?, Opened。Custody=Depositor/Player/StolenByEnemy/Returned/Destroyed。
- ManifestEntry: ThingDefName, StuffDefName?, Quality?, Count。品質は整数としゲーム側で検証。
- StorageSnapshot: MapId, Facilities（FacilityId, Kind, FreeSlots, IsValid）, ValidStoredCargoIds。
- WorldState: SchemaVersion, Contracts, Cargo, Sites, Effects, ReceivableBalance, Debts, FactionBlockedUntil, NextOfferTick, NextSiteTick, SettingsSnapshot。
- Decision: Success, ReasonKey, NewState, Effects。NewStateは更新対象DTO、EffectsはEffectRequest列。実装では型付きDecision<T>で表現してよい。
- EffectRequest: EffectId, Kind, EntityId, Amount, Payload。Kind=DeliverCargo/SpawnSilver/ConsumeCargo/ApplyGoodwill/CreateReturnPod/StartRaid/CreateSite/SpawnReinforcement/CloseSite。
- SettlementResult: AccruedFee, CompletionBonus, CancellationFee, Compensation, PreviouslyClaimed, Credit, Debt, GoodwillDelta。
- RaidContext: NowTick, HostilePresent, LastOrdinaryRaidEndTick?, LastDedicatedRaidStartTick?, ValidAttacker, BaselinePoints, MinimumGroupPoints。
- RaidDecision: Outcome（Start/Defer/Skip）, NextTick?, Points, EffectId。
- SiteRecord: SiteId, FactionId, Tier, Seed, CargoIds, GuardPoints, CreatedTick, FirstEntryTick?, AlarmTick?, VaultStates, ReinforcementApplied, ExpiresTick, Visited, Closed。VaultStatesは金庫IDごとの開錠進捗・収納箱ID・破壊判定適用済み状態。
- SiteContext: NowTick, PlayerPresent, PlayerCaravanEnRoute, Loaded, EligibleTiles, EligibleFactions。
- SavedState: schemaVersionとWorldState相当の保存DTO。WorldSnapshotは有効Map/Faction/Def/Thing IDと消失原因。

ゲーム側のメソッド名は未確認。本計画の業務APIを、存在を確認済みのゲームAPIと混同しない。

## T0 対象ビルドと技術成立性

**Files:** Create `docs/technical/1.7-api-audit.md`。必要なら本仕様・計画を更新。

**Consumes:** 仕様10、利用可能なローカルゲームDLL。
**Produces:** ビルド番号、必要参照、正確な接続シンボル、SDK/TFM、技術ブロッカー。

- [ ] ゲームDLLを読み取り、保存・Quest・Site・Thing所有・キャラバン・襲撃・資産額・配送のシンボルを確認する。ゲーム本体のコードを文書へ転載しない。
- [ ] A00の4条件（資産補正、サイト保存、DLCなし配送、保存境界）それぞれを接続先と検証手順へ対応付ける。
- [ ] DLLがない場合は「環境不足」、成立しない場合は設計変更の必要箇所を記録する。APIを推測して後工程へ進めない。
- [ ] 全条件の接続先が特定でき、参照・SDKが固定されたら文書をコミット。検証用コードが必要なら後続タスクのテストへ含める。

## T1 契約モデルと会計

**Files:** Create `Core/RentalWarehouse.Core.csproj`, `Core/Model.cs`, `Core/Contracts.cs`, `Core/Accounting.cs`, `Core/Settings.cs`, `Tests/RentalWarehouse.Core.Tests.csproj`, `Tests/AccountingTests.cs`, `Tests/ContractTests.cs`, `.gitignore`。

**Consumes:** T0、仕様4/5。
**Produces:** `ContractEngine.Accept(ContractTerms, StorageSnapshot, WorldState, long): Decision`、`ContractEngine.Advance(ContractState, StorageSnapshot, long): Decision`、`Accounting.Settle(ContractState): SettlementResult`。

- [ ] テスト作成: NormalSixDaysのCredit=72、DangerSixDays=252、PartialLossのDebt=34、80銀受領済みなら114。二重枠予約拒否、初期収納前損失、満期固定、負数拒否も固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter "FullyQualifiedName~AccountingTests|FullyQualifiedName~ContractTests"` を実行、未実装による失敗を確認する。
- [ ] 共通DTO、契約遷移、箱別料金、2/4日目支払、返還率・履行係数、取消料、債務、仕様8の設定初期値を実装する。
- [ ] 同コマンドPASS、A02/A03の手計算と一致を確認し、対象ファイルをコミット。

## T2 貨物と施設

**Files:** Create `Core/Cargo.cs`, `Game/RentalWarehouse.csproj`, `Game/Buildings/StorageRack.cs`, `Game/Buildings/Vault.cs`, `Game/Buildings/Reception.cs`, `Game/Integration/CargoAdapter.cs`, `Game/Integration/StorageAdapter.cs`, `Game/Integration/WealthAdapter.cs`, `Mod/About/About.xml`, `Mod/Defs/Buildings.xml`, `Mod/Defs/Cargo.xml`, `Tests/CargoTests.cs`, `GameTests/RentalWarehouse.Game.Tests.csproj`, `GameTests/StorageCases.cs`。

**Consumes:** T1 DTO、T0実API。
**Produces:** `CargoService.Open(CargoRecord, string effectId): Decision`、`StorageAdapter.Capture(string mapId): StorageSnapshot`、`WealthAdapter.CargoValue(CargoRecord): decimal`。

- [ ] CargoTestsで預かり箱価値=0、盗品=DeclaredValue、二度目Openの効果数0、35kg超拒否、施設破壊後の箱数不変を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~CargoTests` で失敗確認。
- [ ] 封印箱、棚、金庫、受付、収納・搬出、誤消費禁止、内容の一回だけの実体化を実装。資産補正の残高部分はT5で完成させる。
- [ ] 同テストPASS。`dotnet build Source/RentalWarehouse/RentalWarehouse.csproj -p:RimWorldManagedPath="<ローカル参照先>"` 成功、A01/A10の施設部分を実ゲーム確認しコミット。参照先は個人環境の値でありリポジトリに固定しない。

## T3 保存と一回だけの副作用

**Files:** Create `Core/Effects.cs`, `Game/Integration/WarehouseWorldComponent.cs`, `Game/Integration/EffectDispatcher.cs`, `Game/Integration/StateCodec.cs`, `Tests/EffectTests.cs`, `Tests/PersistenceTests.cs`, `GameTests/EffectBoundaryCases.cs`。

**Consumes:** T1/T2、T0保存境界。
**Produces:** `EffectLedger.TryBegin(string): bool`、`EffectLedger.Complete(string): void`、`EffectDispatcher.Apply(EffectRequest): bool`、`StateCodec.Export(WorldState): SavedState`、`StateCodec.Import(SavedState): WorldState`。

- [ ] 同IDを2回適用して効果数1、例外後の配送再試行で未配送箱だけ生成、全DTO往復でseed/期日/金額一致をテストする。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter "FullyQualifiedName~EffectTests|FullyQualifiedName~PersistenceTests"` で失敗確認。
- [ ] 台帳保存と副作用を実装。先に適用済みフラグだけ立てて物が消える方式は禁止。生成ThingにEffectIdを対応付け、例外後照合する。
- [ ] 同テストPASS、A11の実ゲーム保存/例外注入を確認してコミット。

## T4 受注・配送・収納クエスト

**Files:** Create `Core/Offers.cs`, `Game/Quests/OfferGenerator.cs`, `Game/Quests/StorageQuest.cs`, `Game/Quests/DeliveryAdapter.cs`, `Game/UI/WarehouseTab.cs`, `Game/UI/OfferView.cs`, `Mod/Defs/Quests.xml`, `Tests/OfferTests.cs`, `GameTests/DeliveryCases.cs`。

**Consumes:** T1 Accept/Advance、T2 Capture、T3 Apply。
**Produces:** `OfferService.Generate(WorldState, long): IReadOnlyList<ContractTerms>`、`DeliveryAdapter.Deliver(ContractState): void`、依頼/契約画面。

- [ ] 営業OFF=0件、初回1日、提示最大4/受注最大3、敵候補なしで高危険度なし、営業再開で再抽選なしを固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~OfferTests` で失敗確認。
- [ ] 受注説明、翌日配送、代替地点、枠予約、全量収納時開始、収納1日期限を接続する。文字列はこの段階から日英キーを使い、T10で全数検査する。
- [ ] 同テストPASS、A01/A14と無効配送地点の実ゲーム確認後コミット。

## T5 支払い・回収・精算

**Files:** Create `Core/Payments.cs`, `Core/Returns.cs`, `Game/Quests/ReturnAdapter.cs`, `Game/Integration/PaymentAdapter.cs`, `Game/UI/AccountingView.cs`, `Game/UI/ContractView.cs`, `Tests/PaymentTests.cs`, `GameTests/SettlementCases.cs`。Modify Accounting.cs、WealthAdapter.cs。

**Consumes:** T1 Settle、T3 Apply、T4契約。
**Produces:** `PaymentService.Claim(WorldState, string mapId, string destinationId): Decision`、`PaymentService.ResolveDebt(WorldState, string debtId, bool refuse): Decision`、`ReturnService.Dispatch(ContractState, IReadOnlyList<string> cargoIds): Decision`。

- [ ] 2日目20/4日目20/最終32、重複Claim追加0、無効地点で残高維持、債務拒否-20/15日停止、同時回収ポッド1個を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~PaymentTests` で失敗確認。
- [ ] 支払可能残高・銀受領・資産補正、回収再要請、3日期限、部分返還、横領・補償・返済/拒否を実装する。返済は銀の消費と債務消込が一回になるようT3を使う。
- [ ] 同テストPASS、A02/A03/A06/A10/A11の会計部分を実ゲーム確認しコミット。経営の一往復が到達点。

## T6 貨物を狙う襲撃

**Files:** Create `Core/Threats.cs`, `Game/Integration/ThreatAdapter.cs`, `Game/Jobs/StealCargo.cs`, `Game/Jobs/ExitWithCargo.cs`, `Mod/Defs/Jobs.xml`, `Tests/ThreatTests.cs`, `GameTests/RaidCases.cs`。

**Consumes:** T1状態、T2貨物、T3 Apply、T5精算。
**Produces:** `ThreatScheduler.Evaluate(ContractState, RaidContext): RaidDecision`、`ThreatAdapter.Start(RaidDecision): bool`。

- [ ] 通常0/1回、高危険度2回、1日間隔、0.5日猶予、延期2日上限、満期後0、開始1/予定2で係数0.5を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~ThreatTests` で失敗確認。
- [ ] seed固定予定、箱奪取・退出、金庫突破、空施設無視、経路不能時代替戦闘を実装。生成失敗を実開始回数に含めない。
- [ ] 同テストPASS、A04/A05と敵退出境界の保存を実ゲーム確認しコミット。

## T7 他派閥の独立金庫

**Files:** Create `Core/VaultSites.cs`, `Game/World/VaultSite.cs`, `Game/World/VaultMapGenerator.cs`, `Game/World/VaultDiscovery.cs`, `Game/UI/VaultInfoView.cs`, `Mod/Defs/WorldObjects.xml`, `Tests/SiteTests.cs`, `GameTests/SiteGenerationCases.cs`。

**Consumes:** T1 DTO、T2貨物、T3保存、T0 Site API。
**Produces:** `SiteService.Create(string siteId, int seed, int tier, SiteContext): SiteRecord`、`SiteService.ShouldClose(SiteRecord, SiteContext): bool`、`VaultMapGenerator.Generate(SiteRecord): void`。

- [ ] 2/4/6箱と各価値範囲、最大3未踏、15日期限/移動中保留、既存集落除外、非敵対初期OFF、再生成で在庫増加0を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~SiteTests` で失敗確認。
- [ ] 独立サイト、金庫室・守備・退出経路を生成。大型は複数金庫。サイトの期限と乱数、初回侵入後のマップ寿命を保存する。
- [ ] 同テストPASS、A07/A09の配置/期限を実ゲーム確認しコミット。

## T8 開錠・略奪・撤退

**Files:** Create `Game/Jobs/UnlockVault.cs`, `Game/World/VaultAlarm.cs`, `Game/World/VaultLootAdapter.cs`, `Game/UI/AttackVaultDialog.cs`, `Tests/LootTests.cs`, `GameTests/VaultRaidCases.cs`。Modify Cargo.cs、VaultSites.cs、WealthAdapter.cs、Jobs.xml。

**Consumes:** T7 SiteRecord、T2 Open、T3 Apply。
**Produces:** `VaultService.Work(SiteRecord, string vaultId, int craftingLevel, long ticks): Decision`、`VaultService.Take(SiteRecord, string cargoId): Decision`、`VaultService.Alarm(SiteRecord, long now): Decision`。

- [ ] 小型工芸0=6,000ticks/工芸10=4,000ticks、警報0.25日/増援1回、同箱二度Take拒否、破壊seed再現、閉鎖後Open可能を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~LootTests` で失敗確認。
- [ ] 金庫ごとの開錠、破壊損失、増援、略奪、盗品価値、開封、敵対化確認、重量を守る搬出を実装する。
- [ ] 同テストPASS、A08/A09/A10/A11の遠征部分を確認しコミット。第三のループが到達点。

## T9 ライフサイクルと途中導入

**Files:** Create `Core/Lifecycle.cs`, `Game/Integration/LifecycleAdapter.cs`, `Game/Integration/Migration.cs`, `Game/Integration/Diagnostics.cs`, `Tests/LifecycleTests.cs`, `GameTests/MigrationCases.cs`。Modify WarehouseWorldComponent.cs。

**Consumes:** T1～T8。
**Produces:** `LifecycleService.Reconcile(WorldState, WorldSnapshot, long): Decision`、`Migration.Upgrade(SavedState): SavedState`。

- [ ] 預け主消滅=無罰取消、プレイヤー地図放棄=補償、欠落Def=対象だけ取消、重複ID=対象隔離、途中導入2回でも初期化1回を固定する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests --filter FullyQualifiedName~LifecycleTests` で失敗確認。
- [ ] 消滅原因別精算、敵対化返還、期限、移行、診断、終端履歴の縮小を実装する。再実行防止IDを消してイベントを復活させない。
- [ ] 同テストPASS、A11/A12/A13、未ロード警報のA09を実ゲーム確認しコミット。

## T10 言語・表示・バランス・配布

**Files:** Create/complete `Game/UI/SettingsView.cs`, `Mod/Languages/Japanese/Keyed/RentalWarehouse.xml`, `Mod/Languages/English/Keyed/RentalWarehouse.xml`, `Mod/Textures/Things/Building/Reception.png`, `Mod/Textures/Things/Building/Rack.png`, `Mod/Textures/Things/Building/Vault.png`, `Mod/Textures/Things/Building/Cargo.png`, `scripts/package.ps1`, `scripts/validate-package.py`, `.github/workflows/validate.yml`, `docs/testing/results-1.7.md`, `docs/credits.md`。Modify READMEと必要なUI/数値。

**Consumes:** T0～T9、A00～A16。
**Produces:** 実測結果、必要ファイルだけのMOD ZIP、プレイヤー向け説明とインストール手順。

- [ ] 言語キー欠落・ゲームDLL混入・中間物混入fixtureを検出する検査を作り、失敗を確認する。
- [ ] 日英と設定を仕上げ、画像を自作または再配布可能な素材で用意。出典/ライセンスを記録する。
- [ ] `dotnet test Tests/RentalWarehouse.Core.Tests` を通し、T2のビルドコマンドへ `-c Release` を加えて成功を確認する。
- [ ] A00～A16の残りを実施し、環境と証跡をresults-1.7.mdに記録する。バランス修正は仕様・数値期待値も更新する。
- [ ] `pwsh -File scripts/package.ps1` と `python scripts/validate-package.py dist/RentalWarehouse.zip` でPASS、ZIPだけでクリーン導入する。
- [ ] CIはworkflow_dispatchのみ。最終時にCore・言語・梱包構成検査を一度実施し、実ゲーム試験の代用にしない。
- [ ] READMEにゲーム内説明、導入方法、対応ビルド、既存セーブ、削除非対応、既知制限を記載してコミット。リリース公開はその時点のユーザー指示に従う。

## 実行結果

2026-10-23にT0～T10を実装した。各タスクの自動テストとビルドは完了、実ゲームを要する確認（各タスク最終ステップの「実ゲーム確認」とA00～A16の実機部分）はゲーム本体のない環境のため未実施で、[検証結果](../../testing/results-1.7.md)に「環境不足」として記録した。チェックリストは実施した範囲の記録として下表にまとめる。

| タスク | 自動テスト | 実ゲーム確認 |
|---|---|---|
| T0 | API監査文書 | 環境不足 |
| T1 | AccountingTests, ContractTests 合格 | — |
| T2 | CargoTests 合格、Game/GameTestsビルド成功 | 環境不足 |
| T3 | EffectTests, PersistenceTests 合格 | 環境不足 |
| T4 | OfferTests 合格 | 環境不足 |
| T5 | PaymentTests 合格 | 環境不足 |
| T6 | ThreatTests 合格 | 環境不足 |
| T7 | SiteTests 合格 | 環境不足 |
| T8 | LootTests 合格 | 環境不足 |
| T9 | LifecycleTests 合格 | 環境不足 |
| T10 | Core全件・梱包検査・ZIP検査 合格 | 環境不足 |

計画完成・実装完了をゲーム動作確認と取り違えない。
