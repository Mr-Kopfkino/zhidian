namespace Qiban.Core;

public sealed class AppState
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long StorageRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? StorageToken { get; set; }
    public int Version { get; set; } = 1;
    public string SelectedFriendId { get; set; } = "default";
    public AppSettings Settings { get; set; } = new();
    public List<FriendProfile> Friends { get; set; } = [new()];
}

public sealed class AppSettings
{
    public string InterfaceTheme { get; set; } = "standard";
    public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1";
    public string Model { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public double Temperature { get; set; } = 0.8;
    public int ContextMessages { get; set; } = 30;
    public int MemoryBudget { get; set; } = 2400;
    public bool SuggestMemories { get; set; } = true;
    public bool AutoMemoryEnabled { get; set; } = true;
    public bool AutoSummaryEnabled { get; set; } = true;
    public int CacheLimitMb { get; set; } = 128;
    public int CacheHours { get; set; } = 24;
    public InitiativeSettings Initiative { get; set; } = new();
}

public sealed class InitiativeSettings
{
    public bool IdleEnabled { get; set; } = true;
    public bool ActivityEnabled { get; set; } = true;
    public int MaxUnanswered { get; set; } = 3;
}

public sealed class VoiceSettings
{
    public VoiceSettings Copy() => (VoiceSettings)MemberwiseClone();
    public bool Enabled { get; set; }
    public bool AutoPlay { get; set; }
    public string Provider { get; set; } = "GPT-SoVITS 内置";
    public string Endpoint { get; set; } = "http://127.0.0.1:9880/tts";
    public string ProtectedApiKey { get; set; } = "";
    public string Model { get; set; } = "tts-1";
    public string Voice { get; set; } = "alloy";
    public string Language { get; set; } = "zh";
    public string ReferenceAudio { get; set; } = "";
    public string ReferenceText { get; set; } = "";
    public string GptWeights { get; set; } = "";
    public string SovitsWeights { get; set; } = "";
    public string RequestTemplate { get; set; } = "{\"text\":\"{{text}}\",\"format\":\"wav\"}";
}

public sealed class JapaneseChatSettings
{
    public JapaneseChatSettings Copy() => (JapaneseChatSettings)MemberwiseClone();
    public bool Enabled { get; set; }
    public string DisplayLanguage { get; set; } = "简体中文";
}

public sealed class FriendProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = DefaultCharacters.KotoriName;
    public string Tagline { get; set; } = "";
    public string Persona { get; set; } = DefaultCharacters.KotoriPersona;
    public string Greeting { get; set; } = DefaultCharacters.KotoriGreeting;
    public string AvatarPath { get; set; } = "";
    public string PortraitPath { get; set; } = "";
    public string ChatBackgroundPath { get; set; } = "";
    public VoiceSettings Voice { get; set; } = new();
    public JapaneseChatSettings JapaneseChat { get; set; } = new();
    public List<ChatMessage> Messages { get; set; } = [];
    public List<MemoryItem> Memories { get; set; } = [];
    public MemoryLearningState Learning { get; set; } = new();
    public InitiativeProfile? InitiativeProfile { get; set; }
    public string InitiativeProfileKey { get; set; } = "";
}

public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string JapaneseText { get; set; } = "";
    public string DisplayLanguage { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Status { get; set; } = "complete";
    public string Trigger { get; set; } = "";
}

public sealed class MemoryItem
{
    public string Origin { get; set; } = "manual";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Content { get; set; } = "";
    public string Category { get; set; } = "偏好";
    public string State { get; set; } = "pending";
    public bool Pinned { get; set; }
    public double Confidence { get; set; } = 1;
    public string SourceMessageId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
