# SlackDrive

PowerShell Provider for Slack workspaces. Navigate channels, users, and messages like a file system.

## Why SlackDrive?

The most popular Slack module for PowerShell, [PSSlack](https://www.powershellgallery.com/packages/PSSlack/), has not been updated since 2021 and relies on [deprecated Slack APIs](https://api.slack.com/changelog/2020-01-deprecating-antecedents-to-the-conversations-api) (`channels.*`, `groups.*`) that no longer work with new Slack apps. SlackDrive uses the modern `conversations.*` API and provides a unique file-system navigation experience.

## Features

- Navigate Slack workspaces as PowerShell drives (`ls`, `cd`, `Get-Content`, `New-Item`)
- Browse channels, private channels, DMs, users, and files
- Read message threads by navigating into messages
- Post messages and thread replies with `New-Item`
- Search users with `Find-SlackUser`
- Tab completion for channel names, user names, and messages
- Automatic two-tier caching (memory + disk) for performance
- OAuth 2.0 with PKCE support (via GitHub Pages relay)
- Multiple workspace support (one drive per workspace)
- Optional [SecretManagement](https://github.com/PowerShell/SecretManagement) integration for secure token storage

## Installation

```powershell
# From PowerShell Gallery
Install-PSResource SlackDrive
```

### Build from Source

```powershell
git clone https://github.com/yotsuda/SlackDrive.git
cd SlackDrive
.\build.ps1
Import-Module .\module\SlackDrive.psd1
```

## Connection Methods

SlackDrive supports three connection methods. Choose the one that fits your environment.

### Method A: OAuth PKCE (Recommended)

Authenticate via browser. No token stored on disk. Best for personal workspaces or workspaces where the admin has approved the app.

#### 1. Create a Slack App

1. Go to https://api.slack.com/apps
2. Click **Create New App** > **From scratch**
3. Enter an app name and select your workspace

#### 2. Set Redirect URL

Go to **OAuth & Permissions** > **Redirect URLs** and add:

```
https://yotsuda.github.io/SlackDrive/oauth/callback.html
```

This is a static page hosted by SlackDrive that relays the OAuth callback to your local machine. PKCE ensures security -- the auth code alone cannot be used to obtain a token.

> **Note:** Manual configuration of User Token Scopes is not required. SlackDrive automatically requests the necessary scopes during the PKCE flow.

#### 3. Configure SlackDriveConfig.json

Copy the **Client ID** and **Client Secret** from the **Basic Information** page.

```powershell
Edit-SlackConfig
```

```json
{
  "PSDrives": [
    {
      "Name": "Slack",
      "Description": "My workspace",
      "ClientId": "YOUR_CLIENT_ID",
      "ClientSecret": "YOUR_CLIENT_SECRET",
      "Enabled": true
    }
  ]
}
```

#### 4. Mount

```powershell
Import-SlackConfig
cd Slack:\
```

A browser window opens on first access for OAuth authentication. After approval, the token is used for the current session.

---

### Method B: User OAuth Token

Copy a user token (`xoxp-`) from the Slack App settings. Use this when PKCE is blocked by workspace admin policy.

#### 1. Create a Slack App (same as Method A)

#### 2. Configure User Token Scopes

Go to **OAuth & Permissions** > **User Token Scopes** and add:

- `channels:read`, `channels:history` -- Channels
- `groups:read`, `groups:history` -- Private channels
- `im:read`, `im:history` -- Direct messages
- `mpim:read`, `mpim:history` -- Group DMs
- `users:read` -- User list
- `files:read` -- File list
- `chat:write` -- Post messages (optional)

Click **Install to Workspace** (or **Reinstall**) after adding scopes.

#### 3. Copy the Token

Copy the **User OAuth Token** (`xoxp-...`) from the **OAuth & Permissions** page.

#### 4. Mount

```powershell
New-SlackDrive -Name Slack -Token 'xoxp-...'
```

Or add to the config file:

```json
{
  "PSDrives": [
    {
      "Name": "Slack",
      "Token": "xoxp-...",
      "Enabled": true
    }
  ]
}
```

> **Tip:** Use [SecretManagement](https://github.com/PowerShell/SecretManagement) to avoid storing tokens in plain text:
> ```powershell
> Set-SlackDriveSecret -Name "MySlackToken" -Token "xoxp-..."
> New-SlackDrive -Name Slack -SecretName "MySlackToken"
> ```

---

### Method C: Browser Token (No App Required)

Use a token from the Slack web client. No Slack App creation needed. Tokens are short-lived (hours to days).

#### 1. Get Token and Cookie

1. Log in to Slack in your browser
2. Open DevTools (F12) > **Network** tab
3. Select a request with `conversation` in the URL
4. Copy the `token` value (`xoxc-...`) from the **Payload** tab
5. Go to **Application** tab > **Cookies** > copy the `d` value (`xoxd-...`)

#### 2. Mount

```powershell
New-SlackDrive -Name Slack -Token 'xoxc-...' -Cookie 'd=xoxd-...'
```

> **Note:** Browser tokens expire when the Slack session ends. Do not store them in config files.

## Usage

### Drive Structure

```
Slack:\
+-- Channels\
|   +-- general\
|   |   +-- 0329_0820_4959_alice_Hello everyone\    <- message (cd into for thread)
|   |       +-- 0329_0825_1234_bob_Great idea       <- thread reply
|   +-- random\
+-- DirectMessages\
+-- Users\
|   +-- alice
|   +-- bob
+-- Files\
```

### Browsing

```powershell
# List channels
ls Slack:\Channels

# List messages in a channel
ls Slack:\Channels\general

# Navigate into a message to see thread replies
cd Slack:\Channels\general\0329_0820_4959_alice_Hello
ls

# Read message text
Get-Content Slack:\Channels\general\0329_0820_4959_alice_Hello

# Get channel details
Get-Item Slack:\Channels\general

# List users
ls Slack:\Users

# Refresh cache
ls Slack:\Channels -Force
ls Slack:\Users -Force
```

### Posting Messages

```powershell
# Post to a channel
New-Item Slack:\Channels\general -Value "Hello from PowerShell!"

# Reply to a thread
New-Item Slack:\Channels\general\0329_0820_4959_alice_Hello -Value "Thread reply"
```

### Searching Users

```powershell
# Search by name (uses Slack's internal search API)
Find-SlackUser john
```

### Multiple Workspaces

```json
{
  "PSDrives": [
    { "Name": "Work", "ClientId": "...", "ClientSecret": "...", "Enabled": true },
    { "Name": "Personal", "Token": "xoxp-...", "Enabled": true }
  ]
}
```

```powershell
Import-SlackConfig
ls Work:\Channels
ls Personal:\Channels
```

## Requirements

- PowerShell 7.4+
- .NET 9.0

## License

[MIT](LICENSE)
