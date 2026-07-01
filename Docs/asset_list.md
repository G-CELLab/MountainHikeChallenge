# Mountain Hike Challenge — Asset List & 3D Model Sourcing

## Sourcing Philosophy

Per Taehyun's guidance, this is a prototype. Visual polish is not the priority. When a realistic model is not available or feasible:

- Replace with a simplified primitive (cube, cylinder, sphere) with a descriptive material/color
- Use placeholder geometry with a label until a real model is sourced
- Never block development waiting for a perfect asset — build with what exists and upgrade later

All models should be evaluated for URP compatibility before import. Built-in RP shaders will cause GrabPass errors in this project.

---

## Environment Assets

### Mountain Trail (Scenes 1, 5)

| Asset | Source | Status | Notes |
|---|---|---|---|
| Low Poly Environment — Nature Free | Polytope Studio (Unity Asset Store, free) | To import | URP compatible, confirmed. Provides trees, terrain, rocks, foliage for Mount Timpanogos exterior. Open sample scenes from this package only in its own test project — the sample scenes use Built-in RP shaders that cause console errors in this URP project. Use meshes/models only, reassign materials to URP Lit. |
| Mountain terrain | Unity Terrain tool | Build in-engine | Use Unity's built-in terrain system for the main hiking path |
| Skybox | Unity built-in HDRI | Included in URP template | Mountain/outdoor skybox |

### Body Interior Environments (Scenes 2–4)

These environments need to feel like the inside of a living organism — organic, slightly glowing, warm/cool color palette depending on the system.

| Asset | Source | Status | Notes |
|---|---|---|---|
| Chest cavity environment | Build in Unity | Prototype | Large rounded interior space, semi-transparent walls showing the body outline outside |
| Lung interior | Build in Unity | Prototype | Branching organic space, alveoli clusters as sphere groups |
| Small intestine interior | Build in Unity | Prototype | Tunnel environment with textured walls |

---

## Organ Models

Priority: Circulatory scene first, then Respiratory, then Muscular.

### Heart

| Option | Source | Notes |
|---|---|---|
| Realistic heart model | Unity Asset Store — search "human heart 3D" | Check URP compatibility and poly count |
| Simplified placeholder | Unity primitive (sphere + smaller sphere, red material) | Use for prototype if no asset found quickly |
| Recommended free source | Sketchfab — search "heart anatomy low poly" | Check license (CC0 or CC-BY preferred) |

The heart does NOT need to stay inside the body during the interaction. Per Taehyun's suggestion, it can be placed as a standalone large object in the scene that the student interacts with directly.

### Lungs

| Option | Source | Notes |
|---|---|---|
| Lung model | Unity Asset Store or Sketchfab | Search "lungs anatomy" |
| Simplified placeholder | Two large ellipsoid meshes, blue material | Acceptable for prototype |
| Diaphragm | Flattened dome mesh or Unity primitive | Simple dome shape is sufficient |

### Alveoli

| Option | Source | Notes |
|---|---|---|
| Alveoli cluster | Group of sphere primitives, varying sizes, semi-transparent | Build in Unity for prototype |
| O₂ / CO₂ particles | Unity Particle System | Red spheres for O₂, gray spheres for CO₂ |

### Muscle Fiber Rig

| Option | Source | Notes |
|---|---|---|
| Muscle fiber | Cylinder meshes in parallel, stretched, red/pink material | Build in Unity — grab points on each end |
| Leg muscle (external) | Simple capsule mesh, green material | Visible in Circulatory scene distance |

### Skeletal System

| Option | Source | Notes |
|---|---|---|
| Knee joint | Unity Asset Store — search "knee joint anatomy" | For Scene 1 optional build |
| Simplified joint | Two cylinder meshes meeting at a pivot point | Prototype placeholder |

### Small Intestine

| Option | Source | Notes |
|---|---|---|
| Intestine model | Unity Asset Store or Sketchfab — search "intestine anatomy" | |
| Simplified placeholder | Tube mesh (cylinder chain), pink textured material | Acceptable for prototype |

---

## Blood and Circulatory System Assets

| Asset | Build Approach | Notes |
|---|---|---|
| Blood packet | Sphere primitive, red emissive material, ~0.3 unit radius | Spawn on heart squeeze, travel along path |
| Artery tubes | Cylinder chain or ProBuilder spline tube, warm red material | Static in scene, packets travel along them |
| Vein tubes | Same as artery, darker red/blue material | Return path from organs back to heart |
| Blood vessel (alveoli) | Small cylinder meshes attached to alveoli clusters, slightly glowing | O₂ transfer destination |

---

## AI Guide Character

The AI guide needs a humanoid or semi-humanoid model with:
- A rigged skeleton supporting arm/hand gestures and pointing
- A face or indicator showing it is "speaking" (even if just a light pulse or text bubble for the prototype)
- Neutral, non-threatening visual design appropriate for middle school students

| Option | Source | Notes |
|---|---|---|
| Custom AI agent (from prior project) | Import via Unity Package Export | Tonmoy's existing character — primary option |
| Unity humanoid placeholder | Unity Asset Store — Starter Assets humanoid | Fallback if import issues arise |
| Robot/abstract character | Unity Asset Store — search "VR character" | Alternative if humanoid feel is not needed |

---

## Audio Assets

| Sound | Source | Notes |
|---|---|---|
| Heartbeat | Freesound.org (CC0) | Rhythmic pulse, speeds up with interaction |
| Blood flow | Freesound.org (CC0) | Low whoosh, loops |
| Breathing | Freesound.org (CC0) | Inhale/exhale, speeds up with climbing |
| Muscle contraction | Freesound.org (CC0) | Soft tension sound |
| Joint click | Freesound.org (CC0) | Short click for skeletal locking |
| Electrical pulse | Freesound.org (CC0) | Nervous system signal |
| Success chime | Freesound.org (CC0) | Positive feedback on mini-game complete |
| Mountain ambience | Freesound.org (CC0) | Wind, birds, outdoor atmosphere |
| AI guide voice | TTS service or pre-recorded | Needs to be warm, calm, encouraging |

---

## UI Assets

| Asset | Build Approach |
|---|---|
| Body dashboard HUD | Build in Unity — World Space Canvas, 2D graphic |
| Body outline silhouette | SVG-style 2D sprite or simple line renderer |
| System indicator lights | Colored circle sprites, material color change on activation |
| O₂ meter | Unity UI Slider, custom skin |
| Energy meter | Unity UI Slider, custom skin |
| Heart rate meter | Unity UI text + animated pulse graphic |

---

## Asset Pipeline Notes

- All imported models need URP materials reassigned after import if they came from a Built-in RP source
- Use ProBuilder (included in Unity) for custom geometry that needs to be built in-engine
- Keep poly counts low — this is a VR experience and frame rate matters more than visual fidelity
- All audio should be mono or stereo WAV/OGG, not MP3 for Unity performance
- Texture sizes: maximum 1024x1024 for prototype assets, 512x512 preferred for environment props
- Check asset licenses before import — prefer CC0, CC-BY, or Standard Unity Asset Store EULA assets