# Making custom scorebugs

A custom scorebug replaces the game's own score clock movie with one drawn entirely in ActionScript. Done so far:
ESPN+ (`mods\espnplus`) and NHL on Prime Video (`mods\prime`, with its own start screen). 24Kit's Scorebug tab edits
them live, and `.nhl24bug` packs share them.

## 1. Where the scorebug lives

- `nhl/ui/swf_assets/kettle/overlays/scoreclock/scoreclock.gfx` and `score_clock.gfx`. The game plays
  `scoreclock.gfx`. Replace both.
- They're Scaleform movies (`.gfx`, "CFX" = zlib-compressed SWF with Scaleform tags 1000+). The main timeline is
  empty: the AS3 class `ScoreClock` builds everything.
- Overlay textures are PS4 GNF files (256-byte header, data tiled in 8×8 Morton tiles). The Worker's `gnf-png` and
  `png-gnf` convert them.
- Team logos come from `ui/textures/common/teamlogos/medium/t<id>`.

## 2. The interface the game calls

`ScoreClockOverlay` (in `nhluidata.swf`) drives the movie through `sendMessage`. A replacement must keep this
public interface:

```
init(data{teamColor*, teamSecondaryColor*, logoPath*, logoID*, cityName*, teamName*, ...}, audioFn, signalFn)
setAwayTeamName / setHomeTeamName, setScores([away, home], anim), setScoresWithDelay
setPeriod, setTime, showClock / hideClock
showGoalWithDelay(side, delay), showTimedGoal, hideGoal
showWarning(side, text, b, b), hideWarning, updatePowerplayTime        (gets the whole phrase: "POWER PLAY 1:33")
showPopup / updatePopup / hidePopup
showDropdown(side, text, info{goalLabel, scorer, value, assistsLabel, assists}), hideDropdown
showComparison([away, home, label]), hideComparison
showShootout / updateShootoutData / hideShootout
setPracticeText, setRivalsMedallion, setIsDebug, dispose
```
HOME = 0, AWAY = 1, NEUTRAL = 2.

Behaviour the movie has to handle itself:
- The game **never hides** the dropdown, comparison or timed goal. The movie auto-hides them (the original used
  timers of about 2.5–4.5 s).
- The game moves the movie around per camera, so the bug pins itself to the top-left of the screen every frame.
- The TeamIntro overlay isn't used in offline games, so a start screen (Prime) lives inside the scorebug and
  shows on the first `showClock`. `init()` carries city and team names. The arena comes from a table in the movie.

## 3. Writing one

Start from an existing `ScoreClock.as` (same interface) and redraw it in code: shapes, gradients, text fields. Logos
(shield, network marks) are vector data converted from SVG.

Scaleform and FFDec quirks:
- No working `RegExp`: parse strings by hand.
- The FFDec (JPEXS) compiler is experimental: **closures and `switch` on strings break silently.** Use named
  methods and if/else.
- Fonts: the movie's `DefineFont3` tags are swapped for Figtree (SIL OFL), so text uses fonts embedded in the movie.

## 4. Building

1. FFDec `-replace` the ScoreClock script in the original movie and swap the fonts →
   `score_clock_<name>.gfx` (keep the CFX compression).
2. Worker `gfx-swf` makes a preview SWF with the images embedded.
3. Copy both into `App\Scorebug` and `App\Preview`, and the gfx into `mods\<name>`.

## 5. Settings block (live editing)

The movie contains one 4096-byte ASCII string constant that starts with `#NHL24KIT_CFG#` (u30 length prefix
`0x80 0x20`). Every setting (position, scale, sizes, colours, text sizes, labels, timings, per-team colours) is
read from it at start. Worker `scorebug-settings` rewrites the string in place, so the movie never needs
recompiling to change settings. The preview passes the same text as the `cfg=` FlashVar and the movie rebuilds.
Schema keys in the app (`ScorebugSettings`) must match the defaults in `loadConfig`.

**Preview:** Ruffle 0.6 inside WebView2. FlashVars `nhl24kitPreview=1` make the movie draw itself with sample data
(`zoom`, `instant`, `state=pp,pk,shots,info,goal,popup`). Use `instant=1` for stills: the Browser pane pauses
animation in the background.

## 6. In 24Kit

- **Scorebug tab:** edit with a live preview, then Save to mod list.
- **Packs** (`.nhl24bug`, a zip; `ScorebugPack.cs`): manifest, settings, team colours, optional extra mods (their
  files copied in), and `preview.png` (captured from the preview). Export pack / Import pack.
- **Scorebug manager:** picks packs from a folder (default `%APPDATA%\NHL24Kit\scorebugs`). Cards show the preview,
  name and details. Use (switching replaces any other pack's entries), Save current here, Delete, Choose folder.

## 7. Other places to hook in

- `vvms/gameintro/intrologooverlay.lua` plays a match-start video via `bnd_video_path`
  (`nhl/data/common/movie/transitions/match_start_*.vp6`). It's empty in offline modes: a possible hook for city
  intro videos.
- `nhluidata.swf` must keep its exact size when edited: patch bytecode in place, recompress tighter, zero-pad.
