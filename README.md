# MapLens V5.4

MapLens is a simple map-run tracker for ExileAPI.

It tracks useful information such as kills, map time, DPS, damage, XP, portals,
deaths, boss status, and gold.

V5.4 adds easy mouse positioning for both panels, a close button on the
hideout summary, and freezes the boss fight timer as soon as the boss is
confirmed dead.

## Installation

1. Download and extract `MapLens_V5.4_Release.zip`.
2. Place the `MapLens` folder inside ExileAPI's `Plugins\Source` folder.
3. Start ExileAPI and enable **MapLens** in the F12 menu.

## Display modes

- **Hideout Summary Only** — shows a vertical summary after returning to hideout.
- **Compact HUD Only** — shows a small panel while mapping.
- **Compact HUD + Hideout Summary** — enables both panels.

Hideout Summary Only is the default.

## Moving or closing panels

- In MapLens settings, enable **Edit panel positions with the mouse**. Drag the
  small **DRAG** strip at the top of either visible panel, then disable the
  option when you are done.
- Click the **X** in the top-right of a hideout summary to dismiss it early.

## Hideout summary

After returning from a map, MapLens shows a clean vertical summary on the left.

![MapLens hideout summary](Images/hideout-summary.png)

## In-map HUD

The optional compact HUD shows your current run information while mapping.

![MapLens compact in-map HUD](Images/in-map-hud.png)

## Notes

- DPS and damage are estimates based on visible monsters losing health.
- UP is the percentage of map time spent in active combat.
- Boss fight time is frozen on the first confirmed boss death.
- Run history resets when ExileAPI closes.
- Position, size, colors, displayed stats, and summary duration can be changed
  in the settings.

Made by Woogo. Pls message on Discord for problems or suggestions <3
