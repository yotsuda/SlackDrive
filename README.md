# SlackDrive

PowerShell Provider for Slack workspaces. Navigate channels, users, and messages like a file system.

## Why SlackDrive?

The most popular Slack module for PowerShell, [PSSlack](https://www.powershellgallery.com/packages/PSSlack/), has not been updated since 2021 and relies on [deprecated Slack APIs](https://api.slack.com/changelog/2020-01-deprecating-antecedents-to-the-conversations-api) (`channels.*`, `groups.*`) that no longer work with new Slack apps. SlackDrive uses the modern `conversations.*` API and provides a unique file-system navigation experience.

## Features

- Navigate Slack workspaces as PowerShell drives (`ls`, `cd`, `Get-Content`)
- Multiple workspace support (one drive per workspace)
- Tab completion for channel and user names
- Automatic two-tier caching (memory + disk) for performance
- OAuth 2.0 with PKCE support
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

## Quick Start

```powershell
# Create a drive for your workspace
New-SlackDrive -Name MySlack -Token "xoxb-your-bot-token"

# List top-level items
ls MySlack:\

# List channels
ls MySlack:\channels

# List users
ls MySlack:\users

# Read messages from a channel
ls MySlack:\channels\general

# Get channel info
Get-Item MySlack:\channels\general

# Get user info
Get-Item MySlack:\users\john.doe

# Read a message thread
Get-Content MySlack:\channels\general\1030_john.doe_hello
```

## Getting a Bot Token

1. Go to https://api.slack.com/apps
2. Create a new app (or select existing)
3. Go to **OAuth & Permissions**
4. Add Bot Token Scopes:
   - `channels:read` - List public channels
   - `channels:history` - Read channel messages
   - `groups:read` - List private channels
   - `groups:history` - Read private channel messages
   - `users:read` - List users
   - `search:read` - Search messages
5. Install to workspace
6. Copy the Bot User OAuth Token (`xoxb-...`)

## Secure Token Storage (Optional)

SlackDrive supports [Microsoft.PowerShell.SecretManagement](https://github.com/PowerShell/SecretManagement) for encrypted token storage. Tokens are never written to disk in plain text when using this approach.

```powershell
# One-time setup
Install-PSResource Microsoft.PowerShell.SecretManagement
Install-PSResource Microsoft.PowerShell.SecretStore   # or any vault extension

# Save your token securely
Set-SlackDriveSecret -Name "SlackToken-MyWorkspace" -Token "xoxb-your-bot-token"

# Create a drive using the stored secret
New-SlackDrive -Name MySlack -SecretName "SlackToken-MyWorkspace"
```

> **Note:** Without SecretManagement, the `-Token` parameter accepts a plain-text token directly. If you use the auto-mount configuration (`SlackDriveConfig.json`), tokens are stored in plain text in the config file. Ensure appropriate file permissions on that file.

## Drive Structure

```
Slack:\
├── channels\
│   ├── general\
│   │   └── [messages]
│   └── random\
├── users\
│   ├── john.doe
│   └── jane.smith
└── files\
```

## Auto-Mount Configuration

Create `SlackDriveConfig.json` in your PowerShell module directory to auto-mount drives on module import:

```json
{
  "rateLimitDelayMs": 1000,
  "cacheExpiryMinutes": 5,
  "psDrives": [
    {
      "name": "MySlack",
      "token": "xoxb-...",
      "enabled": true
    }
  ]
}
```

## Requirements

- PowerShell 7.4+
- .NET 9.0

## License

[MIT](LICENSE)
