# SlackDrive 導入・構成ガイド

## 前提知識

### 必須

| 知識 | 説明 |
|------|------|
| **PowerShell の基本操作** | `ls`, `cd`, `Get-Item`, `Get-Content` など標準コマンドの使い方 |
| **PSDrive の概念** | PowerShell では「ドライブ」はファイルシステムに限らない。`C:\` のように `Slack:\` をマウントし、同じコマンドで操作できる仕組み |
| **Slack アプリの作成** | https://api.slack.com/apps からアプリを作成する手順 |

### あると便利

| 知識 | 説明 |
|------|------|
| **Slack API のスコープ** | トークンに付与する権限（`channels:read` 等）の意味 |
| **OAuth 2.0** | 機密アプリでのユーザー認証フロー |
| **SecretManagement モジュール** | トークンを暗号化保存する Microsoft 公式モジュール。使わなくても動作する |

---

## 導入手順

### Step 1: 環境を確認する

```powershell
# PowerShell 7.4 以上が必要
$PSVersionTable.PSVersion
```

### Step 2: モジュールをインストールする

```powershell
Install-PSResource SlackDrive
```

### Step 3: Slack アプリを作成する

1. https://api.slack.com/apps にアクセス
2. **Create New App** → **From scratch** を選択
3. アプリ名とワークスペースを指定して作成

---

## 認証方式

SlackDrive は 3 つの認証方式をサポートしています。

### 方式 1: 機密アプリ + ユーザートークン（推奨）

OAuth 2.0 で **ユーザー権限** のトークン (`xoxp-`) を取得します。
自分が参加しているチャンネルだけが表示され、スコープで権限を制限できます。

#### Slack アプリの設定

1. アプリの設定画面で **OAuth & Permissions** を開く
2. **User Token Scopes** に必要なスコープを追加:

| スコープ | 用途 | 必須 |
|----------|------|------|
| `channels:read` | パブリックチャンネル一覧 | はい |
| `channels:history` | メッセージ読み取り | はい |
| `groups:read` | プライベートチャンネル一覧 | 任意 |
| `groups:history` | プライベートチャンネルのメッセージ | 任意 |
| `users:read` | ユーザー一覧 | はい |
| `search:read` | メッセージ検索 | 任意 |
| `channels:join` | チャンネルへの参加 (`Join-SlackChannel`) | 任意 |

3. **Basic Information** ページから **Client ID** と **Client Secret** を控える
4. **OAuth & Permissions** の **Redirect URLs** に `http://localhost:8765/slack/callback` を追加
5. ワークスペースにインストール

#### 構成ファイルの設定

```powershell
# 構成ファイルを開く
Edit-SlackConfig
```

```json
{
  "PSDrives": [
    {
      "Name": "MySlack",
      "ClientId": "YOUR_CLIENT_ID",
      "ClientSecret": "YOUR_CLIENT_SECRET",
      "Description": "My Slack workspace",
      "Enabled": true
    }
  ]
}
```

スコープをカスタマイズする場合:

```json
{
  "PSDrives": [
    {
      "Name": "MySlack",
      "ClientId": "YOUR_CLIENT_ID",
      "ClientSecret": "YOUR_CLIENT_SECRET",
      "Scopes": "channels:read channels:history users:read",
      "Enabled": true
    }
  ]
}
```

#### ドライブのマウント

```powershell
# 構成ファイルからマウント（初回はブラウザが開いて OAuth 認証を求められる）
Import-SlackConfig
```

初回実行時にブラウザが開き、Slack の認証画面が表示されます。
**Allow** をクリックするとトークンが取得され、ドライブがマウントされます。

---

### 方式 2: Bot Token

Bot 用のトークン (`xoxb-`) を直接指定します。
セットアップが簡単ですが、`is_member` が Bot の参加状況を示すため、
チャンネルのフィルタリングは利用できません。

#### Slack アプリの設定

1. **OAuth & Permissions** → **Bot Token Scopes** に以下を追加:
   - `channels:read`, `channels:history`, `groups:read`, `groups:history`, `users:read`
2. ワークスペースにインストール
3. **Bot User OAuth Token** (`xoxb-...`) をコピー

#### ドライブのマウント

```powershell
New-SlackDrive -Name MySlack -Token "xoxb-your-bot-token"
```

または構成ファイルに記載:

```json
{
  "PSDrives": [
    {
      "Name": "MySlack",
      "Token": "xoxb-your-bot-token",
      "Enabled": true
    }
  ]
}
```

> **注意:** この方法ではトークンが構成ファイルに平文で保存されます。
> SecretManagement を使う方法については後述の「トークンの安全な管理」を参照してください。

---

### 方式 3: ブラウザセッション（最終手段）

ブラウザの Slack セッション情報を使って**ユーザー権限**でアクセスします。
Slack アプリを作成する権限がない場合の回避策です。

#### トークンと Cookie の取得方法

1. ブラウザで Slack ワークスペースにログインする
2. DevTools を開く（`F12`）
3. **Network** タブを開き、フィルタに `api` と入力
4. Slack 上で何か操作してAPI リクエストを発生させる
5. 任意の API リクエストをクリック

**Token:** Payload タブ → `token` フィールド（`xoxc-...`）をコピー

**Cookie `d`:** Headers タブ → `Cookie` ヘッダから `d=...;` の値をコピー。
または Application タブ → Cookies → `https://app.slack.com` → `d` の値

#### ドライブのマウント

```powershell
New-SlackDrive -Name MySlack -Token "xoxc-..." -Cookie "d=xoxd-..."
```

> **WARNING / 警告:**
>
> **この機能は、Slack が OAuth 2.0 の非機密（パブリック）クライアントフローを現在サポートしていないことへの暫定的な回避策として実装されたものです。使用には細心の注意を払ってください。**
>
> - `xoxc-` トークンは必ず Cookie `d` とセットで使う必要があります。Cookie なしでは認証エラーになります。
> - このトークンは**あなた自身のユーザー権限**で動作します。メッセージの閲覧だけでなく、**あなたの名前での投稿・編集・削除を含むすべての操作が可能**です。AI ツールと組み合わせて使用する場合、意図しない投稿が行われるリスクがあります。
> - **SlackDrive の利用を終えたら、ブラウザで Slack からサインアウトしてセッションを無効化してください。** トークンと Cookie はサインアウトするまで有効であり、漏洩した場合に第三者があなたになりすますことが可能です。
> - ブラウザセッションのトークンは Slack の公式 API 利用規約の想定外の使用方法です。ワークスペース管理者のポリシーに違反する可能性があります。
> - **この機能の使用により生じたいかなる損害についても、作者は一切の責任を負わず、何らの補償も行いません。すべて自己責任で使用してください。**

---

## 基本操作

```powershell
# ドライブのルートを見る
ls MySlack:\

# チャンネル一覧（ユーザートークン: 参加チャンネルのみ / Bot: 全チャンネル）
ls MySlack:\channels

# 全チャンネル（ユーザートークンで未参加を含む）
ls MySlack:\channels -All

# ユーザー一覧
ls MySlack:\users

# チャンネルのメッセージを見る
cd MySlack:\channels\general
ls

# メッセージのスレッドを読む
Get-Content .\1030_john.doe_hello

# チャンネルやユーザーの詳細情報
Get-Item MySlack:\channels\general
Get-Item MySlack:\users\john.doe
```

---

## 複数ワークスペースの同時利用

ドライブ名を変えれば、複数のワークスペースを同時にマウントできます。
各ドライブは独立した接続・キャッシュを持つため、互いに干渉しません。

```powershell
# マウント中のドライブを確認
Get-PSDrive -PSProvider Slack
```

---

## チャンネルの参加・離脱

```powershell
# チャンネルに参加（タブ補完で未参加チャンネルを選択可能）
Join-SlackChannel channel-name

# チャンネルから離脱
Exit-SlackChannel channel-name

# 別のドライブを指定
Join-SlackChannel -Drive OtherSlack channel-name

# 確認してから実行
Join-SlackChannel channel-name -WhatIf
```

> **注意:** `channels:join` スコープが必要です。

---

## トークンの安全な管理

### SecretManagement を使う（Bot Token 向け）

```powershell
# 1. モジュールをインストール（初回のみ）
Install-PSResource Microsoft.PowerShell.SecretManagement
Install-PSResource Microsoft.PowerShell.SecretStore

# 2. トークンを保存
Set-SlackDriveSecret -Name "SlackToken-MySlack" -Token "xoxb-your-bot-token"

# 3. 保存したトークンでドライブをマウント
New-SlackDrive -Name MySlack -SecretName "SlackToken-MySlack"
```

### 機密アプリの client_secret について

構成ファイルに `clientSecret` を平文で保存します。
構成ファイルのアクセス権限を適切に設定してください。

```powershell
# 構成ファイルの場所
# Windows: Documents\PowerShell\Modules\SlackDrive\SlackDriveConfig.json
```

---

## PowerShell プロファイルで自動マウント

```powershell
# $PROFILE を編集
notepad $PROFILE
```

追加する内容:

```powershell
# 構成ファイルからマウント
Import-SlackConfig

# または SecretManagement で個別にマウント
# New-SlackDrive -Name MySlack -SecretName "SlackToken-MySlack"
```

---

## ドライブの取り外し

```powershell
# 特定のドライブを取り外す
Remove-PSDrive -Name MySlack

# 全 Slack ドライブを取り外す
Get-PSDrive -PSProvider Slack | Remove-PSDrive
```

---

## トラブルシューティング

### 「Token parameter is required」と出る

`New-SlackDrive` に `-Token` も `-SecretName` も指定していません。どちらかを指定してください。

### 「invalid_auth」と出る

トークンが無効です。Slack アプリの設定画面でトークンを再確認してください。
アプリをワークスペースに再インストールするとトークンが変わるので注意。

### OAuth 認証でブラウザが開かない

構成ファイルに `clientId` が正しく設定されているか確認してください。

### OAuth で「redirect_uri_mismatch」エラー

Slack アプリの **Redirect URLs** に `http://localhost:8765/slack/callback` が登録されていません。

### チャンネルが表示されない

Bot Token の場合: Bot がそのチャンネルに参加していない可能性があります。
Slack 上でチャンネルにアプリを追加してください: チャンネル設定 → **Integrations** → **Add apps**

ユーザートークンの場合: `ls -All` で全チャンネルを表示してみてください。

### SecretManagement で「vault not found」と出る

SecretStore の初期設定がまだです:

```powershell
Register-SecretVault -Name "Default" -ModuleName Microsoft.PowerShell.SecretStore -DefaultVault
```

### タブ補完が遅い

初回はキャッシュ構築のため API を呼びます。2回目以降は高速になります（5分間キャッシュ）。
`Update-SlackCache` で事前にキャッシュを構築することもできます。
