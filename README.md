# SpreaderBar 1.0.0

A spreader bar for **Secret Flasher Manaka**: cuffs on both ankles hold her legs apart at the bar's width, whatever she does.
Like the game's handcuffs, but for the legs.

## Requirements
- Secret Flasher Manaka (tested with v1.1.3)
- BepInEx 6 for IL2CPP (tested with build 738)
- **RestraintHub 1.0.0** or newer

## Install
Extract the zip into the game folder (the folder with `SecretFlasherManaka.exe`). You get `BepInEx/plugins/SpreaderBar/`.

## How to use
Hold a ring key (e.g. `1`), page with `Z` / `X` to the restraint page and click **Spreader Bar**. With it selected in the
worn list (`B` selects):

| Key | Does |
|---|---|
| `+` / `-` | wider / narrower (1 cm per press, hold to repeat) |
| hold `F` | struggle free |

She can still walk, crouch and crawl, but not run. The narrower the bar, the slower she walks. The cuffs are sized to her
ankles.

## Settings
`BepInEx/config/FlanderDev.SFM.SpreaderBar.cfg` (created on the first start). Most useful:

| Setting | Default | |
|---|---|---|
| Bar.WidthCm | 25 | width (`+` / `-` change it) |
| Bar.MinWidthCm / MaxWidthCm | 16 / 60 | range of `+` / `-` |
| Walk.SpeedAtMinWidth / SpeedAtMaxWidth | 0.35 / 0.6 | walking speed at the narrowest / widest bar (1 = normal speed) |
| General.Mode | LegsApart | `SpreadLegWalk` also keeps her in the spread-leg walk pose |
| Unlock.SuccessChance | 1 | chance that struggling frees her (1 = first try) |

## Good to know
- Whether the bar is on is saved with the game; the width is a setting, the same in every save.
- At normal walking speed the feet slide a little (the walk animation's steps are longer than the bar allows).
- The cuffs are fitted barefoot; with boots or socks they may sit less well.

Single-player / offline only. Code: MIT licence (`LICENSE.txt`). Model: "Rigged Spreader bar" by CableNSFW, updated by Crute, CC BY-ND 4.0 (smutba.se; details in `LICENSE-MODELS.txt`).
