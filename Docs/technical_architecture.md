# Mountain Hike Challenge — Technical Architecture

## Project Structure

```
Assets/
  _Project/
    Scripts/
      Core/
        AIGuideController.cs
        AIGuideDialogue.cs
        AIGestureAnimator.cs
        SceneTransitionManager.cs
        MiniGameSequencer.cs
      MuscularSystem/
        MuscleFiberGrab.cs
        MuscularNarrator.cs
      CirculatorySystem/
        HeartPumpInteraction.cs
        CirculatoryNarrator.cs
      RespiratorySystem/
        DiaphragmInteraction.cs
        RespiratoryNarrator.cs
      Shared/
        MiniGameEvents.cs
        MiniGameState.cs
    Scenes/
      Bootstrap.unity
      MountainTrail.unity
      MiniGame_Muscular.unity
      MiniGame_Circulatory.unity
      MiniGame_Respiratory.unity
      Summit.unity
    Prefabs/
      Core/
        AIGuide.prefab
        SceneTransitionTrigger.prefab
        BodyDashboardHUD.prefab
      MuscularSystem/
        MuscleFiberRig.prefab
      CirculatorySystem/
        HeartModel.prefab
        BloodPacket.prefab
        ArteryTube.prefab
      RespiratorySystem/
        DiaphragmModel.prefab
        AlveoliCluster.prefab
    Audio/
    Models/
    Materials/
    Animations/
    UI/
```

---

## Shared Contract — MiniGameEvents.cs

This file must be agreed upon and committed before any interaction or narrator scripts are built. It defines the events that the XR interaction system fires and the AI/narrator system listens for. Both sides of the project depend on this interface — agree on it first.

```csharp
using UnityEngine;
using System;

public static class MiniGameEvents
{
    // Fired when the student begins a gesture interaction
    public static event Action OnInteractionStarted;

    // Fired when a gesture reaches a meaningful threshold
    // (e.g. heart squeezed hard enough, muscle fiber pulled far enough)
    public static event Action<float> OnThresholdReached; // float = 0-1 progress

    // Fired when a mini-game is successfully completed
    public static event Action<string> OnMiniGameComplete; // string = system name

    // Fired when a mini-game fails or times out
    public static event Action<string> OnMiniGameFailed;

    // Fired when the student looks at a specific region of the environment
    // Used to trigger guide attention-directing lines
    public static event Action<string> OnStudentLookAt; // string = region name

    // Fired when a blood packet arrives at a destination
    public static event Action<string> OnBloodPacketDelivered; // string = destination

    // Invoke helpers
    public static void TriggerInteractionStarted() => OnInteractionStarted?.Invoke();
    public static void TriggerThresholdReached(float progress) => OnThresholdReached?.Invoke(progress);
    public static void TriggerMiniGameComplete(string system) => OnMiniGameComplete?.Invoke(system);
    public static void TriggerMiniGameFailed(string system) => OnMiniGameFailed?.Invoke(system);
    public static void TriggerStudentLookAt(string region) => OnStudentLookAt?.Invoke(region);
    public static void TriggerBloodPacketDelivered(string destination) => OnBloodPacketDelivered?.Invoke(destination);
}
```

---

## Shared Contract — MiniGameState.cs

```csharp
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
    Digestive
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
```

---

## System Architecture

### AI Guide / Narrator Layer
- AIGuideController.cs — drives the guide's state machine (idle, speaking, gesturing, waiting for student)
- AIGuideDialogue.cs — handles triggering lines and audio at the right moments based on events
- AIGestureAnimator.cs — plays specific gestures (point to chest, pump motion, trace spine)
- SceneTransitionManager.cs — handles moving between scenes
- MiniGameSequencer.cs — tracks which mini-games are complete and what comes next
- Per-system narrator scripts (CirculatoryNarrator.cs, etc.) — listen to MiniGameEvents and trigger the appropriate guide lines

**Listens to:**
- MiniGameEvents.OnInteractionStarted → trigger guide "good, now watch" line
- MiniGameEvents.OnThresholdReached → update guide encouragement based on progress
- MiniGameEvents.OnMiniGameComplete → trigger success dialogue and scene transition
- MiniGameEvents.OnBloodPacketDelivered → trigger guide "look — it arrived" line
- MiniGameEvents.OnStudentLookAt → trigger attention-directing lines when student looks at a region

### XR Interaction Layer
- XR rig setup and controller/hand input
- All gesture detection and threshold logic
- Per-system interaction scripts (HeartPumpInteraction.cs, DiaphragmInteraction.cs, MuscleFiberGrab.cs)
- Physics and collider setup on interactive objects
- Blood packet spawning and movement along artery tube paths
- Body region gaze detection

**Fires:**
- MiniGameEvents.TriggerInteractionStarted() when student begins grabbing/squeezing
- MiniGameEvents.TriggerThresholdReached(progress) as gesture progresses
- MiniGameEvents.TriggerMiniGameComplete(systemName) when success condition is met
- MiniGameEvents.TriggerBloodPacketDelivered(destination) when a packet arrives at lungs or legs
- MiniGameEvents.TriggerStudentLookAt(regionName) when gaze detection fires

---

## Scene-by-Scene Technical Notes

### Bootstrap.unity
- Loads persistent systems: AudioManager, SceneTransitionManager, MiniGameSequencer
- Immediately loads MountainTrail scene additively or transitions to it
- No visible content

### MountainTrail.unity
- Mountain trail environment using Polytope Studio low-poly assets
- First-person XR rig walking along trail
- Trigger volumes at key points (easy walk zone, steep incline zone, portal trigger)
- Portal object with transition trigger that calls SceneTransitionManager
- AI guide spawns at portal trigger point

### MiniGame_Circulatory.unity (Priority 1 — build first)
- Chest cavity environment
- Heart model as central interactive object — HeartPumpInteraction.cs attached
- Artery tube objects running toward lung positions (upper environment) and leg muscle positions (lower environment)
- BloodPacket prefab pooled and spawned on each successful squeeze
- Lung objects in upper environment — illuminate when blood packets arrive
- Leg muscle objects in lower environment — illuminate and pulse when blood packets arrive
- O₂ meter UI element tied to delivery rate
- HUD body outline with colored system indicators
- CirculatoryNarrator.cs listening to events and triggering guide lines

### MiniGame_Respiratory.unity (Priority 2)
- Lung interior environment
- Diaphragm model as primary interactive object — DiaphragmInteraction.cs attached
- Alveoli cluster objects — interactive with swipe gesture for O₂/CO₂ exchange
- O₂ and CO₂ particle objects
- Blood vessel objects leading away from alveoli, connecting visually to heart tube direction
- RespiratoryNarrator.cs listening to events

### MiniGame_Muscular.unity (Priority 3 — optional for prototype)
- Leg muscle interior environment
- Muscle fiber rig objects — MuscleFiberGrab.cs attached
- Skeletal bone/joint objects in background
- MuscularNarrator.cs listening to events

### Summit.unity
- Return to mountain exterior
- Full dashboard showing all completed systems
- Homeostasis "system check" interaction
- Celebration/fireworks on completion

---

## Key Technical Decisions

### Blood Packet Approach
Following Taehyun's simplification guidance, blood is represented as discrete visible packets (glowing spheres) rather than continuous fluid. Each heart squeeze spawns a packet that travels along a predefined path (using an animation curve or DOTween path) toward both the lungs and the legs simultaneously. This is simpler to implement than fluid simulation and more visually readable for students.

### Artery Tubes
Artery tubes are static mesh objects (cylinder chains or spline-based tubes) with a glowing/pulsing material. They exist permanently in the environment — blood packets travel along them. This avoids runtime mesh generation.

### HUD Dashboard
Implemented as a World Space Canvas attached to the XR camera rig (not Screen Space, which does not work well in VR). The body outline is a simple 2D graphic. System indicators are colored overlays that activate via script when MiniGameEvents fire.

### Gaze Detection for Look-At Triggers
A simple gaze ray from the XR camera. When the ray hits a collider tagged with a BodyRegion value for a defined duration (e.g. 1.5 seconds), MiniGameEvents.TriggerStudentLookAt fires. Narrator scripts use this to trigger guide attention-directing lines ("good — you found the lungs").

### Simplification Policy
Per Taehyun's guidance: if a realistic 3D model is not available or feasible within the timeline, replace with a simplified primitive. A cylinder with a red glowing material can represent the heart for the prototype. The interaction mechanic and the learning moment matter more than visual accuracy.

---

## Timeline (Six Weeks)

| Week | Focus |
|---|---|
| 1 | Project setup, shared contract (MiniGameEvents, MiniGameState), import AI agent, confirm 3D assets |
| 2 | MiniGame_Circulatory — HeartPumpInteraction + blood packet system, CirculatoryNarrator + HUD |
| 3 | MiniGame_Circulatory polish + testing, begin MiniGame_Respiratory |
| 4 | MiniGame_Respiratory complete, begin MountainTrail scene and transition |
| 5 | Summit scene, full flow playthrough, begin MiniGame_Muscular if time permits |
| 6 | Bug fixing, playtesting, documentation |