# OAuth (PKCE) セットアップ手順

ブラウザトークン (xoxc-) ではなく、OAuth PKCE フローでユーザートークンを取得する方法。

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

Slack の Web UI は HTTPS の URL しか受け付けないため、`Add-SlackRedirectUrl` を使って HTTP localhost を登録する。

### 3a. ブラウザトークンで一時的にマウント

ブラウザの DevTools からトークンと Cookie を取得する:

1. ブラウザで Slack ワークスペースにログイン
2. DevTools (F12) → **Network** タブ
3. URL に `conversation` を含むリクエストを選択
4. **Payload** タブの `token` フィールドからトークン (`xoxc-...`) を取得
5. DevTools → **Application** タブ → **Cookies** → `d` の Value を取得

```powershell
New-SlackDrive -Name TmpSlack -Token 'xoxc-...' -Cookie 'd=xoxd-...'
```

### 3b. Redirect URL を登録

App ID は https://api.slack.com/apps の App 設定ページ URL または **Basic Information** に記載されている。

```powershell
Add-SlackRedirectUrl -AppId YOUR_APP_ID
```

デフォルトで `http://localhost:8765/slack/callback` が登録される。

> **注意:** `Add-SlackRedirectUrl` は Slack の非公開 API を使用しているため、将来動作しなくなる可能性がある。
> その場合は以下の手動手順で登録できる:
>
> 1. https://api.slack.com/apps/YOUR_APP_ID/oauth を開く
> 2. **Redirect URLs** で `https://localhost:8765/slack/callback` を追加して Save する（HTTPS で一旦登録）
> 3. DevTools (F12) → **Network** タブを開く
> 4. 再度 Save URLs をクリックし、Network に表示されたリクエストの URL とペイロードを確認する
> 5. **Console** タブで以下を実行し、HTTP に書き換えて登録する:
> ```javascript
> const formData = new FormData();
> formData.append('token', 'Payload の token の値');
> formData.append('app_id', 'YOUR_APP_ID');
> formData.append('redirect_urls', 'http://localhost:8765/slack/callback');
> fetch('/api/developer.apps.oauth.addRedirectUrls', {
>     method: 'POST', body: formData
> }).then(r => r.json()).then(console.log);
> ```
> `{ok: true}` が返れば成功。

### 3c. 一時ドライブを削除

```powershell
Remove-PSDrive TmpSlack
```

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
