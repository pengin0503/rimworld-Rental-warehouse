# Rental Warehouse — 1.7 検証結果

記録日: 2026-10-23（2026-10-25にIssue #3〜#7の修正分を追記）。[受入テスト計画](acceptance.md)のA00～A16に対する実施結果。判定は「合格」「不合格」「環境不足」「未実施」のみを使い、実ゲームで確認していない項目を合格にしない。

## 環境

| 項目 | 値 |
|---|---|
| 実施場所 | クラウドコンテナ（Ubuntu 24.04.5、x86_64）。**RimWorldの実行環境なし、GPU・画面なし**。ユーザー提供の実DLL（`Assembly-CSharp` 1.7.9787.38423、`0Harmony` 3.0.0.0-rc.3）と`Data/Core/Defs`で静的・プロセス内の検証のみ実施（提供物はリポジトリ・CIに含めない） |
| SDK | .NET SDK 10.0.402、PowerShell 7.6.8、Python 3.13 |
| ゲーム参照 | `Krafs.Rimworld.Ref` 1.7.4985-unstable（メタデータのみ）、`Lib.Harmony.Ref` 2.3.1 |
| 対象ビルド | RimWorld 1.7.4985（Steam unstableブランチ）。DLCなし構成を想定 |
| MODリスト | Harmony（必須依存）+ Rental Warehouse。他MODとの組合せは未検証 |
| seed・セーブ | 実ゲームなしのため該当なし |

## 自動検証の実測（このコンテナで実行）

| 検査 | コマンド | 結果 |
|---|---|---|
| Coreロジック | `dotnet test Tests/RentalWarehouse.Core.Tests -c Release` | 164/164 合格（Accounting 14、Cargo 12、Contract 26、Effect 8、Lifecycle 21、Loot 12、Offer 12、Payment 16、Persistence 4、Settings 7、Site 16、Threat 16） |
| ゲームDLL | `dotnet build Source/RentalWarehouse/RentalWarehouse.csproj -c Release` | 成功、警告0（参照アセンブリに対するシグネチャ一致のみを示す） |
| 実ゲーム試験DLL | `dotnet build Tests/RentalWarehouse.Game.Tests -c Release` | 成功、警告0。33ケースを収録（実行は未実施） |
| 梱包検査の自己テスト | `python -m unittest discover -s scripts -p "test_*.py"` | 12/12 合格（ゲームDLL混入、開発物混入、秘密情報、言語キー欠落、プレースホルダ不一致、テクスチャ欠落、不正XMLを検出することを確認） |
| 梱包 | `pwsh -File scripts/package.ps1` | `dist/RentalWarehouse.zip` 生成（24エントリ、展開後233,580バイト。ZIPのハッシュはビルド時刻を含むため毎回変わる） |
| 配布ZIP検査 | `python scripts/validate-package.py dist/RentalWarehouse.zip --source Source` | PASS |
| 実DLLでのビルド | `-p:RimWorldManagedPath=<提供DLL>`でMOD本体・ゲーム内試験DLL | 成功、警告0 |
| Harmonyパッチ | 提供DLLとHarmony 3.0を読み込んだプロセスで本MODのパッチを適用 | 6/6適用（回収ポッドの受け入れ判定`StorageSettings.AllowedToAccept(Thing)`を含む） |
| XML定義 | `tools/DefValidator`（ゲームのXMLローダ規則を再現）で`Mod/Defs`を実クラス・`Data/Core/Defs`と照合 | 合格（`disallowedSpecialFilters`の記法誤りを検出・修正した後）。検査ツールのテスト19/19、バニラCore全定義で誤検知0 |
| 参照アセンブリ | DLLメタデータ読取 | RentalWarehouse.dll → System.*、Assembly-CSharp、UnityStubs、0Harmony 2.3.1、RentalWarehouse.Core。Core → System.*のみ |

### Issue #3〜#7の修正（2026-10-25）

| Issue | 修正 | 確認 |
|---|---|---|
| #3 | 履歴整理で、未受領残高を持つ契約を削除しない | Core: 21件×72銀が整理後も1512銀のまま、受領は1回だけ、受領後に20件へ整理。修正前は失敗 |
| #4 | 存在しなくなったマップの契約の残高を、どの拠点の受領でも支払う（`WorldState.ClaimableBalance`）。管理画面にその内訳を表示 | Core: 消失後に別拠点で20銀を1回だけ受領、存在する拠点どうしの残高は分離。修正前は失敗 |
| #5 | 回収ポッドは自契約の返還中の箱だけを受け入れる。運搬判定の共通入口`StorageSettings.AllowedToAccept(Thing)`にHarmonyで条件を追加し、リンクした保管グループでもポッドの条件を守る | 実DLLへのパッチ適用。ゲーム内試験`ReturnPodsAcceptOnlyTheirOwnContract`を追加（未実行） |
| #6 | 棚の固定保管設定で、金庫が必要な箱を除外する特別フィルター`RW_VaultOnlyCargo`を追加（Harmony不要） | DefValidatorで実クラス・Core定義と照合。ゲーム内試験`RackRefusesVaultCrates_HaulingPicksTheFarVault`を追加（未実行） |
| #7 | 到着前の取消・システム取消に専用の理由キーと1回だけの通知を付け、取消も履歴に表示 | Core: 敵対化・消滅・放棄の各経路で理由と通知1件、再処理で重複なし。修正前は失敗 |

GitHub Actionsの`validate`ワークフローはpush・PR・手動で実行する。CIは公開の参照アセンブリだけを使い、提供DLLは使わない（Def参照の照合はローカルのみ）。

## A00～A16

「自動」はCore単体テスト・ビルド・梱包検査で確認した範囲。「実ゲーム」は`Tests/RentalWarehouse.Game.Tests`のDebugAction（開発モード）と手動操作で確認する範囲で、今回はゲームがないため全て環境不足。

| ID | 自動で確認した範囲 | 実ゲーム | 判定 |
|---|---|---|---|
| A00 | 4条件の接続先を特定し、実DLLの実装で挙動を確認、Harmonyパッチの適用とXML定義の読込規則を実DLLで検証（[API監査](../technical/1.7-api-audit.md) §5） | ゲーム起動時のMOD読込（PNG・訳の表示を含む） | 環境不足（静的・プロセス内の確認は合格） |
| A01 | 二重予約拒否、金庫条件、全量収納で開始、1日期限で早期回収、収納前の一部/全損（ContractTests） | 受付必須、ポッド配送、代替地点（DeliveryCases/StorageCases） | 環境不足 |
| A02 | 20/20/32で計72銀、高危険度252銀、210銀（AccountingTests/PaymentTests） | 受領した銀の実体化（SettlementCases） | 環境不足 |
| A03 | 保管料100・完了10・補償144・債務34、受領済み80で債務114、1日棚外で10銀だけ停止・満期不変 | 同上の実ゲーム確認 | 環境不足 |
| A04 | — | 奪取・端越え・奪還、空金庫無視、金庫内直接不可（RaidCases、手動） | 環境不足 |
| A05 | 1日間隔、0.5日猶予、2日延期上限、満期後なし、1/2開始で総210銀（ThreatTests） | 襲撃の実生成（RaidCases） | 環境不足 |
| A06 | 発送前は未返還、同時1ポッド、猶予延長なし、3日で未返還精算、既存損失は早期返還でも補償（PaymentTests） | 回収ポッドの実体・積載・送付（SettlementCases） | 環境不足 |
| A07 | 等級別在庫、最大3未踏、非敵対初期OFF、適格タイルのみ・重複なし（SiteTests）、外交変化の一回性（LootTests） | ワールド配置、既存集落非変更（SiteGenerationCases、手動） | 環境不足 |
| A08 | 工芸0=6,000/工芸10=4,000ticks、警報0.25日・増援1回、30%損失seed再現、二度Take拒否（LootTests） | 開錠ジョブ、搬出、重量（VaultRaidCases、手動） | 環境不足 |
| A09 | 再訪で在庫非増加、未ロード中の警報は次ロードで1回、15日期限/移動中保留、閉鎖後も持出箱が残る（SiteTests/LootTests） | 退出・再訪・マップ再構築（手動） | 環境不足 |
| A10 | 預かり箱0、盗品=申告価値、開封で二重計上なし（CargoTests） | MarketValue/資産再計算への反映、横領UI（StorageCases/VaultRaidCases） | 環境不足 |
| A11 | 同一効果IDの一回性、例外後の未配送分だけ再試行、保存DTO往復（EffectTests/PersistenceTests） | 実セーブ・例外注入（EffectBoundaryCases、手動） | 環境不足 |
| A12 | 預け主消滅=無罰取消、敵対化=3日無罰返還、放棄=損失精算、他MOD由来消失、債権者消滅、拒否-20/15日（LifecycleTests/PaymentTests） | 実際の派閥消滅・マップ放棄（MigrationCases、手動） | 環境不足 |
| A13 | 初期化1回、移行の冪等性、欠落Def=対象のみ取消、重複ID隔離（LifecycleTests） | 既存セーブへの途中導入・2回ロード（MigrationCases、手動） | 環境不足 |
| A14 | 日英キーの完全一致・プレースホルダ一致・コード使用キーの網羅（validate-package） | 1280x720/UI1.0、2560x1440/UI1.5での表示 | 環境不足 |
| A15 | 設計上、250ticks間隔・ID登録の追跡・未ロードマップ破棄 | tick中央値の比較計測 | 環境不足 |
| A16 | ZIPにゲームDLL/開発物/秘密情報なし、必須ファイルあり（validate-package PASS） | ZIPだけでのクリーン導入と三つの遊びの開始 | 環境不足 |

## バランス

数値は仕様の初期値のまま。実プレイでの防衛費との比較を行っていないため調整していない。参考までの計算値（倍率1.0）:

- 通常、1箱1,000銀: 72銀／6日。4箱計2,000銀（各500銀）: 日額36銀、満額259銀（保管216＋完了43.2、端数切捨て）。
- 高危険度、1箱6,000銀（35kg制約を満たす高価値品の場合）: 日額36銀、満額324銀（保管216＋完了108）、襲撃2回（各1.0P）。

## 実ゲームでの確認手順

1. RimWorld 1.7.4985（unstable）とHarmonyを導入し、`dist/RentalWarehouse.zip`を展開した`RentalWarehouse`フォルダをModsへ置く。
2. 開発モードで`Tests/RentalWarehouse.Game.Tests/bin/Release/RentalWarehouse.Game.Tests.dll`を`RentalWarehouse/1.7/Assemblies/`へ一時的に追加する（配布物には含めない）。
3. Debug actions → 「Rental Warehouse tests」の各項目を実行し、ログの`[RW GameTest]`行を記録する。
4. 手動項目（襲撃・遠征・再訪・セーブ/ロード・解像度）を受入計画の手順で実施し、本書の判定を更新する。
