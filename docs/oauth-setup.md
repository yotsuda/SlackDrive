# OAuth (PKCE) セットアップ手順

OAuth PKCE フローでユーザートークンを取得し、SlackDrive を利用する方法。

## 1. Slack App を作成

1. https://api.slack.com/apps を開く
2. **Create New App** → **From scratch** を選択
3. App 名とワークスペースを指定して作成

## 2. Redirect URL を設定

**OAuth & Permissions** → **Redirect URLs** に以下を追加:

```
https://yotsuda.github.io/SlackDrive/oauth/callback.html
```

この URL は SlackDrive が提供する静的ページで、認証コードをローカルの SlackDrive にリレーする。認証コードはブラウザ上でリダイレクトされるだけで、サーバーには記録されない。PKCE により、認証コードだけではトークン取得できないため安全。

> **注意:** User Token Scopes の手動設定は不要。SlackDrive の PKCE フローが必要なスコープを自動的に要求する。

## 3. SlackDriveConfig.json を設定

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

## 4. OAuth PKCE でマウント

```powershell
Import-SlackConfig
cd Slack:\
```

初回アクセス時にブラウザが開き、OAuth 認証画面が表示される。許可すると自動的にトークンが取得され、以降はそのセッション内で利用可能。
