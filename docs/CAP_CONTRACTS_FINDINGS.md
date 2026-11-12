# Salary cap, contracts and start season: findings (2026-11-12)

Read-only investigation. Nothing in the game folders or in `D:\CIMS-test\livecopy` was changed. The analysis ran on the
live 1.60 eboot (`NHL24PC\game-UPDATE\eboot.bin`, SHA-1 `fd8af6d3…`, identical to `reference\eboot-v32.bin`), the
suite's DB copies in `%LOCALAPPDATA%\CenterIceModdingSuite\dbwork\`, and copies of the old saves in
`Desktop\CIMS-Keep-For-Fresh-Install-2026-11-06`. Scratch scripts (T3DB reader, LZ4, xref and disassembly helpers) are in
the session scratchpad, not the repo.

Repo state at the start: only untracked `Assets/`, `Debugging/`, `reference/` and `data/variant_map.json.before-fanatics-ten-eleven`
(left alone).

## 0. Why the 2026-27 cap doesn't show: the patch isn't installed

- The game was **reinstalled on 2026-11-06**. Both `NHL24PC\game\eboot.bin` (base 1.00, 187,071,493 B) and
  `game-UPDATE\eboot.bin` (1.60, 188,338,041 B) are dated 11-06 and are stock V3.2. `NHL24PC-CIMS` (the 11-03 merged
  folder) no longer exists.
- No `eboot.bin.before-cap-2026` exists anywhere (searched Downloads, Desktop, Documents, `D:\CIMS-test`, `C:\dev`). There
  is also no `eboot.bin.orig`.
- The live eboot has **no** 104,000,000 / 76,900,000 as int32. It still has the stock 83,500,000 / 61,700,000 at both sites
  (see 1).
- Every other eboot copy has the same stock values: `D:\CIMS-test\*`, `Debugging\v32-recovery` and `Debugging\new-uniform-integration\apply-target`
  (that copy has a different hash, but the cap bytes are stock).
- So the cap diff the brief asked for can't be produced: the patched file is gone. The emulator always runs
  `game-UPDATE\eboot.bin` over `game\`, so **the 1.60 file is the one to patch**. A patch made only to `game\eboot.bin`
  (base 1.00) would never run.
- Even with the eboot patched, an **existing franchise would not change** if its league settings carry their own cap
  (see 2). The live install currently has only `Profile0`: the old `BeAGM*` saves are in the Keep folder and `D:\CIMS-test\play`.

## 1. Where the cap, floor, max and min live (1.60 eboot)

Address maths: SELF header 0x160, code+rodata segment at file 0x51d80 = vaddr 0, data at file 0xa75cb20 = vaddr 0xaa00000.
**vaddr = file − 0x51d80**. shadPS4 patch address = **0x400000 + vaddr**: the live `user\patches\nhl24.xml` uses
0x068E0770 for file 0x065324F0.

**Repo bug:** `data\nhl24-patches.xml` uses 0x0A5324F0 for the same patch (0x4000000 + file offset), so its addresses
look wrong.

| What | Where (file → vaddr) | Stock | Notes |
|---|---|---|---|
| Runtime cap globals | bss 0xBEBE0C0 floor, 0xBEBE0C4 cap, 0xBEBE0C8 min salary, 0xBEBE0CC max salary, 0xBEBE0D0 (unknown setting) | n/a | Read by ~80 instructions across the franchise/GM code (0x813xxxx–0x88xxxxx). |
| **Static default floor+cap** | `movabs rax, imm64` at file 0x88560CD (vaddr 0x880434D); floor dword at **file 0x88560CF**, cap dword at **file 0x88560D3**; stored to 0xBEBE0C0 by `mov [rip], rax` at vaddr 0x8804377 | 61,700,000 / 83,500,000 | Same static init zeroes 0xBEBE0C8 (min) and 0xBEBE0D0. Base 1.00 eboot: file 0x876F9DF / 0x876F9E3. |
| League-settings override | vaddr 0x8252F95–0x825313F: `if (flag) global = settings[profile*0x318 + off]` | from save/profile | +0x80→floor, +0x84→cap, +0x74→min, +0x78→max, +0x7C→0xBEBE0D0. Flag bytes at +0x12B3…0x12B7; master flag at +0x173C (pointer 0xBEBE360, profile index via 0xBEBE370). **If the flags are set, the settings record wins over the eboot default.** |
| Min/max salary fallbacks | 11 inline pairs `mov ecx,0xFED260 (16.7M)` / `mov eax,0xBD358 (775K)`, used via `cmovne` when 0xBEBE0F0 is null. File offsets (16.7M imm): 0x82A8BA8, 0x82D3502, 0x82D3580, 0x82D377A, 0x82E9B5E, 0x82E9F5E, 0x82EAC17, 0x82EB169, 0x8312581, 0x83135F7, 0x8316FF0; each 775K imm is next to it (e.g. 0x82A8BB7, 0x82D3507). | 16,700,000 / 775,000 | Example at vaddr 0x8256E27: `contractdollars*5000` checked against min. |
| Rodata defaults block | file 0x90C7F5C (vaddr 0x90761DC): cap, floor, 0, min, max, 950,000, then small ints | 83.5M, 61.7M, 0, 775K, 16.7M, 950K | No direct RIP xref or absolute pointer found; probably reached through a base pointer. Patch it too for consistency. |
| Cap growth / rollover | vaddr 0x82F4552–0x82F45B9 | n/a | Each new season adds a growth amount to floor and cap, with a lower bound taken from the stack and an **upper clamp of 0x30D39E58 (819,240,536)**. There's no per-year cap table: growth is computed, so 2027+ caps follow from it. |

Searched and **not found** as an override:
- Cap values in any roster DB (stock or work copies of nhl_game, initial_experience, entry_draft, expansion_draft and
  threes): no table or column holds the cap, floor, max or min.
- `leaguegamemodesvars` (key/value, int16) is empty in stock and too narrow to hold dollars.
- 83.5M and 61.7M don't appear as float or as thousands. 61,700 and 16,700 do appear as int32 in random places
  (coincidences).
- tuning, attrib, ebx, ini and json in Patch/Data were not scanned (budget); the eboot shows where the values come from.

## 2. Saves

- `BeAGM*` / `Profile0` `DATA` = JSON header + LZ4 frames (`04 22 4D 18`). The BeAGM header holds
  `"SeasonYear":20231012` (current franchise date, yyyymmdd) plus team and user names.
- The save's T3DB holds **34 `league*` tables** (savegroup `dynasty`), among them `leaguegmdata` (per-team
  `cbaexpenseyear0-24` and `retainedtradesalaryyear0-24`, the franchise copy of dead cap and retained salary),
  `leaguecontractoffers`, `leaguerfatable`, `leagueplayerbuyouttable`, `leaguefreeagentdata`, `leaguegamemodesvars`,
  `leaguescheduletable` and `leaguerostertable`.
- So a running franchise keeps its own dead cap, retained salary, offers and calendar. The decode wasn't finished (nested
  compression), so it's unconfirmed whether player contracts and the 0x318-byte settings profile are in BeAGM or Profile0.
- **Practical rule:** test eboot and DB cap/contract edits only in a **new** franchise / Be a GM.
- Screens: every GM screen (cap space, contract offers, re-sign, trades, free agency) reads the 0xBEBE0C0..0CC globals
  listed above. Contract UI strings are in rodata around file 0x93C0179 (`PlayerContractSeasonYearBegin/End`,
  `PlayerContractStatusDesc`…).

## 3. Contracts (T3DB, `nhl_game-meta.xml` names)

Player table: `exhibitionplayerbiotable` (in all 5 DBs; nhl_game also has `customplayerbiotable`, which is all zero).

| Field | Bits / range | Meaning |
|---|---|---|
| `contractdollars` | 14 b, 0..16383 | **Cap hit in $5,000 units** (McDavid 2500 = $12.5M, Matthews 2328 = $11.64M). Stock max is 3000 ($15M). 20.8M = 4160 fits. There is no separate salary-vs-cap-hit field. |
| `contractlength` | 4 b | Years remaining (0 = none) |
| `contractstatus` | 3 b, lo −1 | −1 = none / unsigned, 0 = signed (only values seen) |
| `contractis2way`, `contractismultiyear` | 1 b | Two-way flag; multi-year flag |
| `contractneedsentrylevel` | 1 b | Always 0 in stock. ELC isn't flagged explicitly (the game derives it). |
| `extensiondollars` / `extensionlength` / `extensionis2way` | 14 / 4 / 1 b | Already-signed extension, same units |
| `retainedsalarytrades` | 2 b | Times this contract has been retained (0–2) |
| `yearufa`, `yearfirstsigned`, `yearwaivereligible`, `yearprodebut`, `draftyear`, `year` (birth) | 8 b | **Year − 1900** (124 = 2024). RFA/UFA = `yearufa` vs the current season. |

- Team-level `stockteamsalaryextras` (32 rows): `cbaexpenseyear0..24` = dead cap / buyouts in **whole dollars**, year 0 =
  current season (EDM 1,916,667 = Neal buyout); `retainedtradesalaryyear0..24` and `numretainedsalariesyear0..24`.
- Franchise copies of the same are in `leaguegmdata`. Offers and RFA in `leaguecontractoffers` / `leaguerfatable` use a
  **15-bit** `contractdollars` and 16-bit day numbers (`offerdate`, `signeddate`, `enddate`).
- **No NTC/NMC anywhere.** The meta has no clause, no-trade or no-movement field in any table (NHL 27 has
  `contractclause0-9` / `contractclausemntcnumteams*`; NHL 24 doesn't). No spare bit-flag column either.

Samples (stock):

| DB | Player | team | dollars | len | ext | yearufa | waiver |
|---|---|---|---|---|---|---|---|
| nhl_game (stock) | McDavid | 11 | 2500 | 3 | 0 | 124 | 123 |
| nhl_game (stock) | Matthews | 27 | 2328 | 1 | 0 | 124 | 123 |
| initial_experience | McDavid | 11 | 2500 | **4** | 0 | 124 | **122** |
| initial_experience | Matthews | 27 | 2328 | **2** | 0 | 124 | 122 |
| dbwork\nhl_game (other copy) | Matthews | 27 | 2328 | 1 | **2650×4** | 124 | 123 |

- initial_experience is one season "earlier" (2022-23 lengths). entry_draft and expansion_draft match IE; threes matches
  nhl_game.
- The two nhl_game copies differ: `dbwork\nhl_game` has the Aug-2023 Matthews extension. Which is the game copy and which
  is the Patch copy needs confirming against the TOC.
- **Edit all 5 DBs** (and both game and Patch copies), keeping their offsets consistent.

## 4. Start season (2023)

- Schedules (`nhlschedule` etc.) store **month (0-based) and day only**, no year. Every DB year is year − 1900. The year
  comes from code.
- The real source is **league-settings slot +0x6C** (same 0x318 record as the cap), with hard-coded 2023 fallbacks.
  216 validated `0x7E7` instructions were found in code; ranked candidates:
  1. **vaddr 0x81471F1 (file 0x8198F71) `mov eax,0x7E7`**: getter that returns `settings[profile*0x318+0x6C]`, or 2023 when
     there's no profile. Most likely "current/start season year".
  2. **vaddr 0x835268F (file 0x83A440F) `mov r13d,0x7E7`**: same pattern (+0x6C, else 2023).
  3. vaddr 0x8803AF7 (file 0x8855877) and 0x8188392 (file 0x81DA112): `year = 2023 + value/1000`, i.e. franchise dates
     stored as season*1000+day.
  4. vaddr 0x82669BB (file 0x82B873B) `lea edx,[rdx+rsi+0x7E7]` and 0x8266921 `cmp edx,0x7E7`: packed date (year in bits 0–11)
     = 2023 + offset.
  5. vaddr 0x82DD167 (file 0x832EEE7) `add eax,0x7E7`: the same packed-date year compared against `& 0xFFF`.
  6. vaddr 0x89738E4 / 0x89738F0 (file 0x89C5664 / 0x89C5670): 2023 / 2022 age cutoff (Sep 15).
  - The rest (e.g. 0x4FEE4E4 and the `lea [rax+0x7E7]` group in 0x3A…–0x4B…) sit in generic calendar code (div-by-100,
    ×100). Leave them alone; patching all 156/216 is what crashed.
- Not found yet: what initialises slot +0x6C for a new franchise. Next step: xref writes with stride 0x318 / offset 0x6C.

## Proposed fixes

1. **Cap:** re-apply the patch to `game-UPDATE\eboot.bin` (1.60), or better as a runtime patch in `user\patches\nhl24.xml`
   so a reinstall can't wipe it:
   - floor imm32 at file 0x88560CF (addr 0x08C0434F) and cap imm32 at file 0x88560D3 (addr 0x08C04353).
   - the 11 min/max immediate pairs.
   - optionally the rodata block at file 0x90C7F5C (addr 0x0947A1DC).
   - Then test in a **new** franchise. If the value still doesn't show, the settings override (+0x80/+0x84 with the flags)
     is coming from the profile: next, decode Profile0's settings record. Also check that the growth clamp (819M) and
     the rollover don't need changes.
2. **Saves:** treat franchise saves as snapshots. The suite should say "start a new franchise" after cap, contract or
   season edits.
3. **Contracts:** expose the fields above in the suite (UI in $; stored ÷5000; years −1900). Write them to all 5 DBs ×
   game/Patch/TOC copy, plus `stockteamsalaryextras` for dead cap and retained salary. NTC/NMC can't be stored: keep them
   as suite-side metadata only.
4. **Start season:** first test-patch candidates 1+2 (2023→2026) together on a copy, then 3–5 if dates or ages look wrong.
   Shift every DB year−1900 field and contract lengths by the same delta.
5. Fix `data\nhl24-patches.xml` addresses to 0x400000 + vaddr (= 0x400000 + file − 0x51D80).
