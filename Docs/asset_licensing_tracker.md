# Third-Party Asset Licensing & Credits Tracker

Living document — add one row per external asset (3D model, texture, VFX,
audio, font, etc.) the moment you download it, before it ever gets imported
into the project. Easiest time to capture the source/license/creator is the
moment you find it — much harder to reconstruct six weeks later.

**Workflow:** whenever you grab something new, send me the asset name +
source URL + creator (if listed) + the license page, and I'll add a row here
with the requirements already parsed out.

---

## How to read the "Requirements" column

| Requirement | What it means for you |
|---|---|
| **Attribution required** | Must credit the creator somewhere reasonable — see "Where Attribution Actually Goes" below. |
| **Share-Alike (viral)** | If you distribute the project, you're obligated to release it (or at least the relevant part) under the *same* license. This can conflict with keeping a commercial/school product closed — flag these for a real decision, don't just log and move on. |
| **Non-Commercial only** | Can't be used in anything monetized or institutionally funded in a way that counts as "commercial" — worth confirming with whoever owns the project's business side if this ever comes up, definitions of "commercial" vary. |
| **No Derivatives** | Can't modify/retopologize/recolor the asset at all — only usable as-is. |
| **None (CC0 / Public Domain)** | No obligations. Still worth logging so you have a record of where it came from. |

---

## Asset Log

| # | Asset Name | Source / URL | Creator | License | Commercial OK? | Attribution Required? | Share-Alike? | Modifications Allowed? | Notes |
|---|---|---|---|---|---|---|---|---|---|
| 1 | The Human Spinal Column | [sketchfab.com/3d-models/the-human-spinal-column-bcd9eee09ce044ef98a69c315aa792e2](https://sketchfab.com/3d-models/the-human-spinal-column-bcd9eee09ce044ef98a69c315aa792e2) | scratchi (Sketchfab) | CC BY 4.0 | Yes | **Yes** | No | Yes (must note changes) | 332.7k tris / 166.8k verts — see performance note below |
| 2 | Brain | *(paste the Sketchfab model URL — not visible in the screenshot)* | maheshshinde777 (Sketchfab) | CC Attribution (CC BY) | Yes | **Yes** | No | Yes | Downloaded as .fbx (5MB); .glb/.gltf/.usdz also available if fbx import gives you trouble |

---

## License: CC BY 4.0 (Attribution 4.0 International)

**Source you sent:** https://creativecommons.org/licenses/by/4.0/

**You're allowed to:**
- Use it commercially, without restriction.
- Modify/adapt it (retopologize, recolor, resize, rig, whatever the pipeline needs).
- Redistribute it as part of the built project.

**You're required to:**
- Give attribution: creator's name, a copyright notice (if the source provided one), a link back to this license, and a link to the original material.
- Indicate that changes were made, if you modified it (a simple "modified from original" note satisfies this — no need to itemize every change).
- Not imply the original creator endorses your project.

**Not required:**
- No share-alike — your project itself does **not** need to be released under CC BY. This is the one specific advantage this license has over CC BY-SA (like Z-Anatomy) if you're weighing the two.
- No non-commercial restriction — safe for a paid/institutional product.

**Minimal compliant credit line, once you know the model name/creator:**
> "[Asset Name]" by [Creator Name], licensed under CC BY 4.0 ([https://creativecommons.org/licenses/by/4.0/](https://creativecommons.org/licenses/by/4.0/)). Modified for use in this project.

**Credit line for the spinal column model specifically:**
> "The Human Spinal Column" by scratchi, licensed under CC BY 4.0 ([https://creativecommons.org/licenses/by/4.0/](https://creativecommons.org/licenses/by/4.0/)). Modified for use in this project.

**Credit line for the Brain model:**
> "Brain" by maheshshinde777, licensed under CC BY 4.0 ([https://creativecommons.org/licenses/by/4.0/](https://creativecommons.org/licenses/by/4.0/)). Modified for use in this project.

### ⚠️ Performance note — Spinal Column model (332.7k triangles / 166.8k vertices)

That's heavy for a single asset in a standalone VR build, especially if you're
targeting Quest (your package manifest has `meta-openxr`, so that's a real
constraint, not a hypothetical one). For comparison, a whole scene's poly
budget on Quest-class hardware is typically in the low hundreds of thousands
*total* — this one model could eat most of it by itself, before the rest of
the environment, hands, and UI are even counted.

Before importing:
- **Decimate it in Blender** (free) — a spine doesn't need individual vertebra
  detail at the resolution this was scanned/modeled at for a stylized
  educational VR piece. Getting it down to 20-40k triangles is realistic
  without a visible quality loss at arm's length, which is roughly how close
  the player will be to it.
- Since CC BY explicitly permits modification (that's the "Adapt" freedom;
  you just need the "modified" note in the credit, already in the line above),
  decimating it is fully within the license — no separate permission needed.
- Check the import scale/pivot too — Sketchfab exports vary a lot in scale
  conventions, and a mismatched scale is the single most common "why is my
  imported model giant/tiny" issue.

---

## Where Attribution Actually Goes

CC BY doesn't dictate a specific location, just "reasonable manner" — options,
roughly cheapest-to-implement first:

1. **A `CREDITS.md` / `NOTICES.txt` file** in the project repo alongside the build — satisfies the letter of the license but a player never sees it. Fine as a baseline, not a substitute for #2 if you want to be safe.
2. **An in-app Credits screen** — a simple scrollable text panel reachable from a menu (even a hidden/end-of-experience one) is the standard, safe approach for a shipped VR experience. Given the anatomy hike already has a Summit/wrap-up scene, tacking a credits panel onto that flow is a natural, low-effort spot.
3. Both together is the actual safest bet — repo file for your own record-keeping, in-app screen for the "reasonable manner" requirement players actually encounter.

---

## Quick Reference: Common License Types You'll Run Into

| License | Commercial use | Modify | Share-Alike | Attribution |
|---|---|---|---|---|
| **CC0 / Public Domain** | ✅ | ✅ | ❌ | ❌ (not required, but crediting is still polite) |
| **CC BY** | ✅ | ✅ | ❌ | ✅ |
| **CC BY-SA** | ✅ | ✅ | ⚠️ Yes — your derivative must carry the same license | ✅ |
| **CC BY-NC** | ❌ | ✅ | ❌ | ✅ |
| **CC BY-ND** | ✅ | ❌ (no modifications at all) | ❌ | ✅ |
| **CC BY-NC-SA** | ❌ | ✅ | ⚠️ Yes | ✅ |
| **"Royalty-free" (store license, e.g. TurboSquid/CGTrader/Sketchfab Store)** | Usually ✅ | Usually ✅ | ❌ | Check the specific store's terms — not all require it, but some restrict resale of the asset itself even after purchase |

**Red flags to stop and check before importing anything:** "personal use only," "non-commercial," "editorial use only," or any license that isn't a standard CC variant or a clear store purchase agreement — these need a specific read before use, not a skim.