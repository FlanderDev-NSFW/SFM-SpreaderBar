# SpreaderBar

A leg spreader bar for SecretFlasherManaka, used like the game's normal handcuffs but for the legs. **Needs RestraintHub**
(0.7.0+; included in the package): the ring entry, number shortcuts, the list of worn restraints with the selected one, F and + / -
routing and the conflicts with other restraints are the hub's (`mods/RestraintHub/README.md`).

- **Ring menu** (hold 1): entry **"Spreader Bar"**, always owned, on the restraint page after the game's (page 5 with Z/X). Click it
  to lock the bar; choosing it again while it is on selects it in the hub's list. Refused while another restraint holds her legs
  (the wall restraint).
- **Number shortcut:** like any ring entry, release a ring key (1-8) over it to put it on that key (shown bottom right); a short tap of
  the key then locks the bar. Kept in the save and across map changes.
- **Locked (`Mode` LegsApart, default):** the cuffs close around the ankles and the bar holds them **exactly its width apart** on top
  of whatever the player does - standing, walking, crawling, any pose or action; nothing is blocked except running and auto-run.
  Closed, crossing or wider legs all end at the bar width. The bar keeps the animation's direction up to `StrideAngle` (30 deg) away
  from straight across the body, so a step stays a (shorter) step. Each leg is bent with a two-bone solve (hip fixed, knee in its
  plane, foot keeps its angle) after the animation, every frame; when a leg cannot reach (wide bar, straight legs) the hips come down
  just enough for the knees to bend - no bone is stretched and the feet stay on the floor. One lock mode only.
- **Width:** `+` / `-` while the bar is on and selected in the hub's list (also numpad + / -; on a US keyboard the `=` key counts as `+`; the keys are looked up by
  their printed character, so a German "+" key works), 1 cm per press, hold to repeat, from `MinWidthCm` (16) to `MaxWidthCm` (60).
  The tubes are refitted for each width (the telescope always covers it) and the width is saved in the config. A width below what
  the refitted tubes allow is shown clamped but no longer written back (0.7.0: before, the setting crept up on every launch).
- **`Mode` SpreadLegWalk** (the 0.3.0 behaviour): the player is also held in the **Spread-Leg Walk** pose (the bar width applies too). The vanilla pose refuses
  crouching, the handcuffs, peeing, items and switches ("Can't do that now"); the bar makes it step aside for them (ended just before the
  game handles the input) and it returns once the player is idle again (0.4 s). Other poses work too; Shift + move ends them and the
  spread-leg walk comes back. With the game's handcuffs on, the legs still go into the pose (arms stay cuffed behind the back).
- **Unlock:** with the bar selected in the hub's list, hold the interact key (F) for `AttemptSeconds` (4.5 s): one try frees the legs (`SuccessChance` 1). The game's own
  "Unlocking..." gauge shows the progress. A lower `SuccessChance` makes it work like the normal handcuffs (several tries, +2 % per
  failure).
- **Interactions take precedence:** inside an interaction area (closet, PC, machines ...) F never unlocks the bar. With the game's
  handcuffs on too, F goes to whichever of the two is selected in the hub's list (`B` switches). (SpreadLegWalk mode: like vanilla, the pose hides interaction prompts; it steps aside for the
  handcuff struggle and returns when F is released.)
- **Saved** with the game (three keys in the slot's save file; the shortcut key is written by the hub, ignored by the game without the mod); kept across map changes.
  The game stores shortcuts as ring indices, and an index the game does not know breaks loading (the stage never starts). So the save
  never holds the bar's index: a key with the bar keeps the key's previous vanilla entry in the game data, and the mod's own key
  (`SpreaderBar.Shortcuts`) puts the bar back. Without the mod such a key simply shows that vanilla entry again.
  Saves written by 0.1.0 with the bar on a shortcut are repaired on load (a warning is logged).

## Model

`assets/spreaderbar.sbm` is exported from the Blender file (`Spreader_bar_1_1_1.blend`) and embedded in the DLL:

```bash
blender -b "D:/Assets/_3D/Spreader_bar_1_1_1.blend" --python mods/SpreaderBar/tools/export_model.py -- mods/SpreaderBar/assets/spreaderbar.sbm
```

The exporter enables the rig's bevel/subdivision modifiers, splits the model into rigid parts per side (tube, chain, cuff, jaw) by
posing the rig and comparing vertex positions (the left cuff is weighted to the `.R` bones in the source file), fits the jaw hinge from
the motion and checks it reproduces the rig. In game:

- the bar tubes are shortened for the current width (refitted from their original shape on every width change, longest ~67 cm in
  game); only the plain middle of each tube is squeezed, caps and bevels keep their shape;
- each cuff sits square to its shin (`CuffHeight` above the ankle joint) and turns on its eye; the bar runs straight eye to eye;
- the meshes use a copy of the game's handcuff material (UnityChan toon shader), tinted with the rig's colours;
- **each cuff is sized to its leg** (`FitCuffs`): every 2 s the player's skinned meshes near the ankles (body, socks, shoes ...) are
  baked and cut with the cuff plane (three cuts over the ring's height); the outline's half-widths along the bar and across it give
  a per-axis cuff scale (an oval leg gets an oval cuff, up to `MaxOval`) plus `ClearanceCm`. The cuff stays centred on the shin bone;
  the largest outline since the bar was put on is kept (a pose can make the measurement smaller, never the leg). The bar keeps
  `Scale`; its eyes follow the cuffs.

## Config (`BepInEx/config/com.local.spreaderbar.cfg`)

| Key | Default | Meaning |
|---|---|---|
| General.RingLabel | Spreader Bar | ring menu text |
| General.Mode | LegsApart | `LegsApart`: legs held apart on top of any pose; `SpreadLegWalk`: also force the spread-leg walk |
| Unlock.AttemptSeconds | 4.5 | seconds per unlock attempt (applies live) |
| Unlock.SuccessChance | 1 | chance per full attempt (1 = first try; applies live) |
| Model.Scale | 0.75 | bar size (and cuff size when FitCuffs is off: hole ~2.5 cm radius) |
| Model.FitCuffs | true | size each cuff to the leg it closes around |
| Model.ClearanceCm | 0.4 | gap between the leg and the cuff's inner ring |
| Model.MaxOval | 1.3 | how oval a fitted cuff may get (1 = always round) |
| Bar.WidthCm | 25 | distance between the cuff centres (+ / - in game, saved here; applies live) |
| Bar.MinWidthCm / MaxWidthCm | 16 / 60 | range of + / - (the model allows 15.2 to ~67 cm) |
| Bar.StepCm | 1 | change per + / - press |
| Bar.StrideAngle | 30 | how far (deg) the bar may turn from straight across when one foot is ahead; 0 = side by side |
| Model.CuffHeight | 0.085 | metres above the ankle joint (above the shoe) |
| Debug.Disable | 0 | bitmask to switch parts off when hunting a conflict: 16 load hook, 32 save hook, 128 the whole controller, 256 leg spreading (live); 1 / 4 / 8 were the input, label and gauge hooks (the hub's since 0.7.0) |

`SpreaderBar.Status` (public static) shows the live state for diagnostics: `./tools/dev.ps1 game get SpreaderBar.Status::State`.

## Verification

- L1: `tests/SpreaderBar.Tests` (model format, axis conversion incl. winding, tube shortening, pose solve, lock rules).
- L3 (2026-10-11, dev copy, isolated saves): ring entry shown and chosen through the real ring UI; pose forced, running/crouch blocked,
  walking works; cuffs around the ankles (screenshots); F unlock with the game's gauge, success after 6 attempts (chance 0.10 -> 0.20),
  pose released and bar hidden; F in the closet area does not unlock (negative control) while F mid-room does; with the game's handcuffs
  on, F goes to the cuffs and the pose resumes after they come off; locked state saved (closet autosave and `SaveManager.Save`) and
  restored after a restart; kept across a map change (Apart -> Residence).
- L3 0.2.0 (2026-10-11): bar assigned to key 2 through the real ring (release over the entry), tap 2 locks it and starts the pose; tap 3
  (vanilla entry) and a long hold of 2 do not (negative controls); tap while worn: "already worn". A 0.1.0 save holding index 31 on key 2
  (stage broken by `IndexOutOfRangeException` in `RingMenuParentView.Initialize`) loads normally with 0.2.0, key 2 restored. Unlock,
  re-lock by key 2, map change (shortcut kept); bar moved to key 4 and Auto Run onto key 2, save, restart, load: key 4 = bar
  (stored index 3 = the previous Vibrator Switch), key 2 = Auto Run, no exception.
- L3 0.3.0 (2026-10-12): bar locked by key 4; C -> crawling on all fours (no "Can't do that now"), crawl forward, C -> spread-leg walk
  again; Q keeps the pose (negative control); key 6 -> handcuff panel -> "Behind the body" -> cuffed and back in the pose (screenshot);
  F -> the game's handcuff struggle (`UnlockProgress` 0.59, bar stays locked), released -> pose; cuffs off after 4 holds, pose kept;
  one F hold -> bar unlocked on the first try ("success after 0 failures"), pose released; key 5 (Armpit Squat) while locked -> that
  pose, Shift + W -> spread-leg walk again.

- L3 0.4.0 (2026-10-12, LegsApart): cuff distance across the body before -> after the solve (`BarController.LastSpreadBefore/After`):
  standing 0.19 -> 0.217, walking 0.06-0.11 -> 0.217 every sample (screenshots: legs apart, bar ankle to ankle, diagonal in a
  stride), crawling still and moving 0.16 -> 0.217 (no "Can't do that now"; rear and top views: knees on the floor, feet apart, bar
  between the ankles), Armpit Squat on key 5 works with the bar on (0.335 -> 0.287, the bar's longest). Control: with
  `DebugExtraSpread` +0.3 m the legs are visibly wide, with Debug.Disable 256 together again. Writing the bones in `Camera.onPreCull`
  instead did not change the rendered legs (only the bar followed) - fixed by writing them in `LateUpdate`.

- L3 0.5.0 (2026-10-12): `+` (`=` key, held) 25 -> 43 cm, numpad + -> 60 cm (the cap), `-` -> 16 cm, logged `width:` lines and
  `WidthCm` saved in the cfg; `+` with the bar off does nothing (negative control). Cuff distance after the solve = width in every
  sample: standing 25 / 43 / 60 / 16 cm (hips lowered 0 / 1.3 / 5.4 cm, feet on the floor in the screenshots), crawling at 60 cm
  (0.600, no hip drop; wide crawl in the screenshot), Armpit Squat at 30 cm (0.335 -> 0.300), walking at 30 cm (0.300 every sample).

- L3 0.6.0 (2026-10-12): measured leg radius 3.3 / 3.4 cm at cuff height (the default cuff hole is 2.5 cm - the reported clipping);
  fitted half-widths R 2.8 x 3.1, L 3.0 x 3.0 cm -> cuff scale R 0.95 / 1.05, L 1.01 (was 0.75). Screenshots (inset, cropped):
  fitted cuffs are full rings around both ankles; `FitCuffs` off (control) they sink into the skin. Crawling and walking keep the
  fit (2.7-3.3 cm half-widths; without the "keep the largest" rule crawling read 2.1 cm).

## Known limits

- Cuff fit: measured from the body and whatever is skinned to the leg; tested barefoot only (no socks or boots at hand). Rigid
  accessories (MeshRenderer, not skinned) at the ankle are not measured.

- The walk animation's stride is longer than the bar allows, so walking at normal speed slides the feet a little.
- With `StrideAngle` 30 a standing stance with one foot ahead shows the bar slightly diagonal (more visible at wide widths).
- The width is per config, not per save.

- Before 0.7.0 the bar added its own ring entry (index 31, after the game's) and handled + / - and F itself; with the yoke on too,
  one press changed both widths and one F hold freed both. The hub now does the ring and routes the keys to the selected restraint.
