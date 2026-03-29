# SlackDrive 接続ガイド

SlackDrive は3つの接続方法をサポートしている。環境に応じて選択する。

## 方法 A: OAuth PKCE（推奨）

ブラウザで認証し、セッション単位でユーザートークンを取得する。トークンを設定ファイルに保存する必要がない。

### 前提条件

- 自分が管理者のワークスペース、または管理者が App インストールを承認済みであること

### 手順

#### 1. Slack App を作成

1. https://api.slack.com/apps を開く
2. **Create New App** → **From scratch** を選択
3. App 名とワークスペースを指定して作成

#### 2. Redirect URL を設定

**OAuth & Permissions** → **Redirect URLs** に以下を追加:

```
https://yotsuda.github.io/SlackDrive/oauth/callback.html
```

この URL は SlackDrive が提供する静的ページで、認証コードをローカルの SlackDrive にリレーする。PKCE により安全。

> **注意:** User Token Scopes の手動設定は不要。SlackDrive が必要なスコープを PKCE フローで自動的に要求する。

#### 3. SlackDriveConfig.json を設定

**Basic Information** ページから Client ID と Client Secret を取得し、設定ファイルに記載する。

```powershell
Edit-SlackConfig
```

```json
{
  "clientId": "YOUR_CLIENT_ID",
  "clientSecret": "YOUR_CLIENT_SECRET",
  "psDrives": [
    {
      "name": "Slack",
      "description": "My workspace",
      "enabled": true
    }
  ]
}
```

#### 4. マウント

```powershell
Import-SlackConfig
cd Slack:\
```

初回アクセス時にブラウザが開き、OAuth 認証画面が表示される。許可すると自動的にトークンが取得され、以降はそのセッション内で利用可能。

---

## 方法 B: User OAuth Token（トークン直接指定）

Slack App の設定画面からユーザートークン (`xoxp-`) をコピーして使用する。PKCE が使えない環境（管理者承認が得られない等）で有効。

### 手順

#### 1. Slack App を作成（方法 A と同じ）

#### 2. User Token Scopes を設定

**OAuth & Permissions** → **User Token Scopes** に以下を追加:

- `channels:read`, `channels:history` — チャンネル
- `groups:read`, `groups:history` — プライベートチャンネル
- `im:read`, `im:history` — DM
- `mpim:read`, `mpim:history` — グループ DM
- `users:read` — ユーザー一覧
- `files:read` — ファイル一覧
- `chat:write` — メッセージ投稿（任意）

スコープ追加後、**Install to Workspace**（または Reinstall）をクリック。

#### 3. トークンを取得

**OAuth & Permissions** ページに表示される **User OAuth Token** (`xoxp-...`) をコピー。

#### 4. マウント

```powershell
New-SlackDrive -Name Slack -Token 'xoxp-...'
```

または設定ファイルに記載:

```json
{
  "psDrives": [
    {
      "name": "Slack",
      "token": "xoxp-...",
      "enabled": true
    }
  ]
}
```

> **注意:** 設定ファイルにトークンを書く場合、ファイルのアクセス権限に注意すること。安全に保存するには [SecretManagement](https://github.com/PowerShell/SecretManagement) を利用する:
> ```powershell
> Set-SlackDriveSecret -Name "MySlackToken" -Token "xoxp-..."
> New-SlackDrive -Name Slack -SecretName "MySlackToken"
> ```

---

## 方法 C: ブラウザトークン（App 作成不要）

Slack App の作成が不要。ブラウザの DevTools からトークンを取得して使用する。トークンはセッション依存で有効期限が短い。

### 手順

#### 1. トークンと Cookie を取得

1. ブラウザで Slack ワークスペースにログイン
2. DevTools (F12) → **Network** タブ
3. URL に `conversation` を含むリクエストを選択
4. **Payload** タブの `token` フィールドからトークン (`xoxc-...`) を取得
5. DevTools → **Application** タブ → **Cookies** → `d` の Value (`xoxd-...`) を取得

#### 2. マウント

```powershell
New-SlackDrive -Name Slack -Token 'xoxc-...' -Cookie 'd=xoxd-...'
```

> **注意:** ブラウザトークンは短命（数時間〜数日）。期限が切れたら再取得が必要。設定ファイルへの保存は非推奨。
