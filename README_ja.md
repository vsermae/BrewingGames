# 深淵回想 · ルーム制ローグライクRPG

<p align="center">
  <img src="https://raw.githubusercontent.com/vsermae/BrewingGames/main/.github/assets/banner.png" alt="Abyssal Echo Banner" width="960" />
</p>

深淵回想は、地下へと潜っていくことを軸にしたルーム制ローグライクRPGです。各階層の部屋を探索し、戦闘、宝箱、ショップ、祭壇、イベント、ボスと向き合いながら、最深部の第15階層で「深淵の主」を倒します。

- ルーム制マップとランダムイベント
- スキル/防御/回復を組み合わせる戦略的な戦闘
- 装備の強化とビルドの自由度
- Godot 4 + C# による堅牢な実装
- ブラウザや Electron に依存しない Windows / Linux 配布

## ゲームの魅力

### 1. すべての部屋が選択になる

各フロアは自動生成され、次の扉の先には次のような場面が待っています。

- 敵部屋：ターン制バトル
- 宝箱：3つの報酬から1つを選択
- ショップ：ゴールドで装備やアイテムを購入
- 祭壇：強化、浄化、または代償を払って力を得る
- イベント部屋：結果が変わる選択肢
- ボス部屋：階層ごとの守護者

### 2. 戦闘は「攻撃するだけ」では終わらない

Abyssal Echo の戦闘は、状況判断と戦略が重要です。

- 攻撃 / 防御 / スキルのタイミングが勝負を左右する
- 毒、燃焼、凍結、眩暈、装甲破壊などの状態異常が連鎖する
- フロアごとの特殊ルールが戦術を変える
- ボス戦は単純な数値勝負ではなく適応が求められる

### 3. 長く育てやすい設計

このゲームは Godot 4 と C# で開発され、ロジックと描画を分離しています。

- `Core/` と `Systems/` は Godot に依存しにくい構造
- プレイヤー、敵、装備、イベントがデータ駆動で管理
- 自動検証・画面テスト・ベンチマークが開発に組み込まれている

## すぐ遊ぶ

### 必要環境

- Godot 4.7.2 .NET
- .NET SDK 8

### 実行

```bash
cd godot
dotnet build
godot --path .
```

### エクスポート

```bash
cd godot
godot --headless --path . --export-release "Windows Desktop" build/win/AbyssEcho.exe
godot --headless --path . --export-release "Linux" build/linux/AbyssEcho.x86_64
```

## 主な要素

- ルーム生成による深淵探索
- ターン制バトルと状態異常管理
- 装備とインベントリの最適化
- フロア別メカニクスとボス戦
- データ駆動で調整しやすい設計
- Windows / Linux 向けのデスクトップ配布

## ドキュメント

- [README.md](README.md)
- [godot/README.md](godot/README.md)
- [godot/MIGRATION.md](godot/MIGRATION.md)
- [RELEASE_NOTES.md](RELEASE_NOTES.md)

---

深淵へと降りていけ。生き延びて、そして勝ち取れ。
