public enum MiniGameState
{
    NotStarted,
    InProgress,
    Success,
    Failed
}

public enum BodySystem
{
    Muscular,
    Circulatory,
    Respiratory,
    Nervous,
    Skeletal,
    Digestive,
    // Not a mini-game system in the same sense as the six above — nothing to
    // pump/squeeze/pull — but treated as its own system here so it can be
    // marked complete via the same MiniGameEvents.TriggerMiniGameComplete
    // ("Homeostasis") contract and show up in HUD/progress tracking like
    // everything else, per project decision.
    Homeostasis
}

public enum BodyRegion
{
    Heart,
    LungsLeft,
    LungsRight,
    LegMuscleLeft,
    LegMuscleRight,
    Diaphragm,
    AlveoliCluster,
    SpinalCord,
    KneeJointLeft,
    KneeJointRight
}