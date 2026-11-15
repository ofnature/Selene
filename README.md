# Selene

A Dalamud plugin for FFXIV's **Cosmic Exploration**: it runs stellar missions for you, and tries to
do it the way a person plays rather than the way a script does.

## Scope

**What Selene is for.** Taking a job's stellar missions from the board to the report — choosing
one, accepting it, getting the work done, turning it in, and going on to the next — over an
evening that has a shape: bursts of work, breaks, a stop for the night. Several characters can run
at once, each on its own timetable, so they do not start and stop in step.

**What it does itself:**

- Reads the missions: every mission of a job by star and kind, what the game offers now, your best
  on each, what it pays and what it needs unlocked.
- Runs a mission: accept, open the crafting log, report at the rank you set (a Master mission is
  played to its clock).
- Chains missions by a goal: the ones you ticked, gold on every mission, levelling the cosmic tool,
  or class points.
- Looks after the run between missions: repairs at the star's mender, travels to the star you pick.
- Measures what a mission really pays in class points, since the game's data does not say.
- Plans each character's sessions (the bursts and the breaks).

**What it leaves to other plugins.** Selene decides *which* mission and *when*; it does not do the
work inside one.

| Work | Done by |
|---|---|
| The crafts of a mission | [Hephaestus](https://github.com/ofnature/Hephaestus) |
| Gathering and fishing (planned) | [Demeter](https://github.com/ofnature/Demeter) |
| Walking | [Ariadne](https://github.com/ofnature/Ariadne) |
| The trip to a star | Lifestream |

**What it will not do:**

- **The market board is never used** — not bought on, not read.
- It does not promise safety. Automating the game is against its terms of service; playing at a
  person's pace lowers the chance of standing out, it does not remove it. Use it at your own risk.

## Status

Early. Each part below has run in game, some only a few times.

**Working, for the crafting jobs:**

- The Missions page, with a class picker and a tab a star and a kind.
- One mission from a single press of Run, and chains of them in the four modes above, with varied
  pauses between missions.
- **Gold all** turns the board over when nothing it wants is offered (a spare mission is accepted
  and abandoned, or now and then run instead) and waits for the missions the game offers only at
  certain hours or in certain weather.
- Repair at the mender under a gear condition you set (50% by default).
- The trip to a picked star.
- The payout log, with an export as a sheet.
- The session schedule — **shown only**, in the main window and an edge-dock tab.

**Not done yet:**

- Running by the session schedule: today a chain runs until its goal, its limit or Stop.
- Gatherers' and Fisher's missions (listed, not run), and red alert missions.
- Handing in the cosmic tool, food, changing class between missions, moving on to the next star by
  itself.

`/selene` opens the window.

## Installing

Add this custom plugin repository in Dalamud's settings (Experimental → Custom Plugin
Repositories), then install Selene from the plugin installer:

```
https://raw.githubusercontent.com/ofnature/Selene/main/repo.json
```

Selene needs Hephaestus to run a crafting mission, Lifestream for the trip to a star and Ariadne
for the walk to the mender. Without one of them, the part that needs it says so and does nothing.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher).

```
dotnet build Selene.sln -c Release
dotnet test Selene.sln
```
