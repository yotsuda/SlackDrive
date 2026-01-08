# SlackDrive

PowerShell Provider for Slack workspaces. Navigate channels, users, and messages like a file system.

## Features

- Navigate Slack workspaces as PowerShell drives
- Multiple workspace support (one drive per workspace)
- Browse channels, users, and messages with familiar `ls`, `cd`, `Get-Content` commands
- Tab completion for channel and user names
- Automatic caching for better performance

## Installation

```powershell
# Build from source
.\build.ps1

# Import the module
Import-Module .\module\SlackDrive.psd1
```

## Quick Start

```powershell
# Create a drive for your workspace
New-SlackDrive -Name UiPath -Token "xoxb-your-bot-token"

# List top-level items
ls UiPath:\

# List channels
ls UiPath:\channels

# List users
ls UiPath:\users

# Get messages from a channel
ls UiPath:\channels\general

# Get channel info
Get-Item UiPath:\channels\general

# Get user info
Get-Item UiPath:\users\john.doe
```

## Getting a Bot Token

1. Go to https://api.slack.com/apps
2. Create a new app (or select existing)
3. Go to "OAuth & Permissions"
4. Add Bot Token Scopes:
   - `channels:read` - List public channels
   - `channels:history` - Read channel messages
   - `groups:read` - List private channels
   - `groups:history` - Read private channel messages
   - `users:read` - List users
   - `search:read` - Search messages
5. Install to workspace
6. Copy the Bot User OAuth Token (`xoxb-...`)

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

## Requirements

- PowerShell 7.0+
- .NET 9.0

## License

MIT