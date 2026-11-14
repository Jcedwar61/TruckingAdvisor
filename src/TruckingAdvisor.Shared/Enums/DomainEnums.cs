namespace TruckingAdvisor.Shared.Enums;

public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    GamePaused = 3,
    SimulationFallback = 4
}

public enum TelemetryFieldCategory
{
    DirectTelemetry,
    Calculated,
    SimulatedExternal
}

public enum SpeedingSeverity
{
    None = 0,
    GentleAdvisory = 1,     // 1-5 MPH over
    ModerateWarning = 2,    // 6-10 MPH over
    SeriousWarning = 3      // 11+ MPH over
}

public enum AudioPriority
{
    Low = 0,        // Casual CB chatter, achievement announcements
    Normal = 1,     // Delivery progress, rest reminders
    High = 2,       // Major speeding, critical fuel
    Critical = 3    // Dangerous air pressure, serious engine warning
}

public enum CBRadioState
{
    RadioOff = 0,
    RadioIdle = 1,
    IncomingTransmission = 2,
    PlayingTransmission = 3,
    AwaitingPlayerResponse = 4,
    PlayerTransmitting = 5,
    ProcessingSpeech = 6,
    GeneratingResponse = 7,
    PlayingAiResponse = 8
}

public enum HalfDuplexCollisionPolicy
{
    IgnoreUntilClear = 0,
    InterruptSpeaker = 1,
    QueueTransmission = 2
}

public enum RadioActivityLevel
{
    Off = 0,
    Quiet = 1,
    Normal = 2,
    Busy = 3,
    VeryBusy = 4
}

public enum ConversationTimingMode
{
    Brief = 0,
    Natural = 1,
    Chatty = 2,
    Quiet = 3,
    Immersive = 4
}

public enum RelationshipLevel
{
    Stranger = 1,
    FamiliarVoice = 2,
    RoadAcquaintance = 3,
    RoadBuddy = 4,
    OldFriend = 5
}

public enum CoDriverPersonality
{
    Professional = 0,
    Friendly = 1,
    OldSchoolTrucker = 2,
    StrictInstructor = 3,
    Minimal = 4
}

public enum DeliveryState
{
    NoJob = 0,
    JobStarted = 1,
    CargoAttached = 2,
    Underway = 3,
    TrailerDisconnectedMidTrip = 4,
    Completed = 5,
    Cancelled = 6
}

/// <summary>Where spoken lines come from.</summary>
public enum VoiceEngine
{
    /// <summary>Free Microsoft neural voices (needs internet); Windows voices if they can't be reached.</summary>
    FreeNeural = 0,
    /// <summary>Windows voices only (offline).</summary>
    Windows = 1,
    /// <summary>OpenAI voices with your key (costs money); free voices if they fail.</summary>
    OpenAI = 2
}
