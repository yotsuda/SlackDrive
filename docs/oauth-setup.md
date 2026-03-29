# OAuth (PKCE) セットアップ手順

OAuth PKCE フローでユーザートークンを取得し、SlackDrive を利用する方法。

## 1. Slack App を作成

1. https://api.slack.com/apps を開く
2. **Create New App** → **From scratch** を選択
3. App 名とワークスペースを指定して作成

## 2. User Token Scopes を設定

**OAuth & Permissions** → **User Token Scopes** に以下を追加:

- `channels:read` — パブリックチャンネル一覧
- `channels:history` — パブリックチャンネルのメッセージ
- `groups:read` — プライベートチャンネル一覧
- `groups:history` — プライベートチャンネルのメッセージ
- `im:read` — DM 一覧
- `im:history` — DM メッセージ
- `mpim:read` — グループ DM 一覧
- `mpim:history` — グループ DM メッセージ
- `users:read` — ユーザー一覧
- `search:read` — メッセージ検索

## 3. Redirect URL を設定

**OAuth & Permissions** → **Redirect URLs** に以下を追加:

```
https://yotsuda.github.io/SlackDrive/oauth/callback.html
```

この URL は SlackDrive が提供する静的ページで、認証コードをローカルの SlackDrive にリレーする。認証コードはブラウザ上でリダイレクトされるだけで、サーバーには記録されない。PKCE により、認証コードだけではトークン取得できないため安全。

## 4. SlackDriveConfig.json を設定

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

## 5. OAuth PKCE でマウント

```powershell
Import-SlackConfig
cd Slack:\
```

初回アクセス時にブラウザが開き、OAuth 認証画面が表示される。許可すると自動的にトークンが取得され、以降はそのセッション内で利用可能。
