# Rental Warehouse

RimWorld 1.7向けのMODです。他派閥の資産を預かる貸倉庫・貸金庫の経営、預かり資産を狙う襲撃からの防衛、他派閥の独立金庫への遠征を追加します。

> **状態:** 計画T0～T10の実装は完了し、Coreロジックの自動テスト（164件）・ビルド・配布ZIP検査は合格しています。**実際のゲーム上での動作確認はまだ行っていません**（開発環境にゲーム本体がないため）。詳細は[検証結果](docs/testing/results-1.7.md)を参照してください。

## 遊び方

- **経営:** 受付台（その他タブ）を建てて「営業する」をオンにします。翌日から2日ごとに依頼が届き、管理タブ「貸倉庫」で内容・日額・完了報酬・最大補償・襲撃予定を確認して受注します。貨物は翌日ポッドで届くので、1日以内に屋根のある屋内の棚（通常貨物）か金庫（高危険度貨物）へ全箱収納すると6日の期間が始まります。2日目・4日目に受領可能残高が増え、管理タブから銀として受け取れます。満期後は回収ポッドを要請し、箱を積んで「返送」すると精算されます。
- **防衛:** 預かった貨物を狙う専用の襲撃が来ます。敵は無傷の金庫に入っていない箱を1人1箱ずつ奪ってマップ端へ逃げます。端を越える前に倒すか取り戻せば契約は続き、越えられた箱は紛失として補償が必要になります。
- **遠征:** 営業開始2日後から5日ごとに、他派閥の独立した金庫施設の情報が届きます（既存の集落は変更しません）。キャラバンで向かい、金庫を開錠（工芸技能で速くなる）するか破壊（中身の一部が失われる）して箱を運び出します。箱はそのまま持ち帰るか、その場で開封できます。退出しても在庫・守備・開錠進捗は保たれ、補充はされません。

開封した預かり箱は横領になり、価値の100%の補償と友好度-30になります。補償を払えない分は債務になり、債務がある間は新規受注できません。

## 必要環境

- RimWorld **1.7.4985**（Steamのunstableブランチ）。1.6以前では動作しません（1.7はWaylith/.NET 10のため別ビルドが必要）。
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)（必須。先に読み込む）
- DLCは不要です。

## インストール

1. リリースの`RentalWarehouse.zip`を展開し、中の`RentalWarehouse`フォルダをRimWorldの`Mods`フォルダへ置きます。
2. MOD一覧でHarmonyの後にRental Warehouseを有効にします。
3. 既存のセーブにも追加できます。追加しただけでは何も起こらず、受付台で営業を始めたときから依頼が届きます。

**MODの途中削除には対応していません。** 契約中・金庫情報がある状態で外すと、封印箱や金庫が読み込めなくなります。

## 設定

MOD設定で、通常/高危険度の依頼、金庫情報、非敵対派閥の金庫（初期OFF）、保管料倍率・襲撃規模倍率（0.25～4.0、0.25刻み）を変更できます。変更は新しい依頼と金庫情報にだけ適用され、進行中の契約は変わりません。

## 既知の制限

- 実ゲームでの受入試験（A00～A16の実機部分）は未実施です。表示崩れ、AIの挙動、バランスは未確認です。
- 1.7の参照アセンブリにはメソッド本体がないため、資産額への反映やHarmonyパッチの適用は実機で確認が必要です（[API監査](docs/technical/1.7-api-audit.md)）。
- Harmonyが読み込めない場合、契約箱のキャラバン除外、回収ポッドの契約別の受け入れ、受領可能残高の資産計上が無効になります（ログにエラーを出します）。
- 他MODとの互換性は検証していません。

## 開発者向け

```sh
dotnet test Tests/RentalWarehouse.Core.Tests                 # Coreロジック（ゲーム不要）
dotnet build Source/RentalWarehouse/RentalWarehouse.csproj   # ゲームDLL（NuGetの参照アセンブリを使用）
pwsh -File scripts/package.ps1                                # dist/RentalWarehouse.zip を作成
python scripts/validate-package.py dist/RentalWarehouse.zip --source Source

# XML定義をゲームのクラスと照合（参照アセンブリでも実DLLでも可。--reference で Def 名の実在も確認）
dotnet run --project tools/DefValidator -- --assembly <Assembly-CSharp.dll> \
  --assembly Mod/1.7/Assemblies/RentalWarehouse.dll --defs Mod/Defs [--reference <RimWorld>/Data/Core/Defs]
```

リリースは、`Mod/About/About.xml`の`modVersion`と`Source/RentalWarehouse/RentalWarehouse.csproj`の`Version`を揃え、`.github/releases/v<版>.md`に告知文を置いてから、GitHubのActions → Release → Run workflow（main、タグ例 `v0.1.0`）で実行します。同じ名前のタグをpushしても実行されます。検査に通った場合だけタグを作り、プレリリースとしてZIPを公開します。

ローカルのゲームDLLで確認する場合は`-p:RimWorldManagedPath=<RimWorldの管理DLLフォルダ>`を付けます。ゲームDLLはリポジトリにもZIPにも含めません。実ゲームの結合試験は`Tests/RentalWarehouse.Game.Tests`（開発モードのDebug actions）で、手順は[検証結果](docs/testing/results-1.7.md)にあります。

| 文書 | 内容 |
|---|---|
| [確定要件](docs/requirements.md) | ユーザーが選択した方針 |
| [仕様書](docs/superpowers/specs/2026-10-23-rental-warehouse-design.md) | 契約・料金・施設・防衛・遠征・保存 |
| [実装計画](docs/superpowers/plans/2026-10-23-rental-warehouse.md) | T0～T10と実行結果 |
| [API監査](docs/technical/1.7-api-audit.md) | RimWorld 1.7の接続先と設計変更 |
| [受入テスト計画](docs/testing/acceptance.md) / [検証結果](docs/testing/results-1.7.md) | A00～A16と実施状況 |
| [クレジット](docs/credits.md) | 素材と依存のライセンス |
