using System.Text.Json.Serialization;

namespace SlackDrive;

public class SlackChannel
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPrivate { get; set; }
    public bool IsArchived { get; set; }
    public bool IsMember { get; set; }
    public string? Topic { get; set; }
    public string? Purpose { get; set; }
    public int MemberCount { get; set; }
    public DateTime Created { get; set; }

    public SlackChannelDetail ToDetail() => new()
    {
        Path = Path, Directory = Directory,
        Id = Id, Name = Name, IsPrivate = IsPrivate, IsArchived = IsArchived,
        IsMember = IsMember, Topic = Topic, Purpose = Purpose,
        MemberCount = MemberCount, Created = Created
    };
}

/// <summary>Get-Item で返す詳細ビュー用サブクラス。Members 列を含む。</summary>
public class SlackChannelDetail : SlackChannel { }

public class SlackUser
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RealName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsBot { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsDeleted { get; set; }
    public string? TimeZone { get; set; }
}

public class SlackMessage
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";
    public string Ts { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? UserName { get; set; }
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? ThreadTs { get; set; }
    public int ReplyCount { get; set; }
    public List<SlackReaction>? Reactions { get; set; }
}

public class SlackReaction
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public List<string> Users { get; set; } = new();
}

public class SlackFile
{
    [JsonIgnore] public string Path { get; set; } = "";
    [JsonIgnore] public string Directory { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string? UrlPrivate { get; set; }
    public DateTime Created { get; set; }
    public string UserId { get; set; } = "";
}

public class SlackSearchResult
{
    public string Query { get; set; } = "";
    public int Total { get; set; }
    public List<SlackMessage> Messages { get; set; } = new();
}