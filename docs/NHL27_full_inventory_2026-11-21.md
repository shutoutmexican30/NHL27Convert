# Summary (read-only investigation, nothing written to the game)

## 1. State of D:\NHL24PC_V3.2
- `NHL24PC\game` and `NHL24PC\game-UPDATE` are **junctions** to `D:\stock-restore\base` and `D:\stock-restore\update-UPDATE`. The live, modded data is physically in `D:\stock-restore` (43.3 + 6.6 GB), despite the folder name. Don't use `stock-restore` as a stock source.
- The suite's settings (`%APPDATA%\CenterIceModdingSuite\settings.json`) still point GameDir/ApplyTargetDir/InPlaceWriteAllowedDirs at `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC`, which no longer exists.
- The repo moved: it's at `D:\dev\NHL27Convert` (branch convert27-slice, HEAD 828577b). `C:\dev\NHL27Convert` holds only a stray 92 MB file. The `C:\dev\NHL27Convert-kits` worktree is gone (git lists it as prunable).
- 64 Fanatics kits: present (style 2, variants 24/25, on all 32 NHL teams, 2 each).
- Fanatics-as-default swap: **not applied** (0 of the Fanatics rows are isdefault=1).
- Contracts: present (about 2,475 players' contractdollars differ from the stock reference).
- Goalie masks: 17 teams' goaliemask fields differ from stock. I only checked the DB side, not the mask textures.
- Number-fix kits: not checked at texture level (the kit check needs a run).
- Salary cap patch: **not in the active** `user\patches\nhl24.xml` (0 cap entries). It's only in `nhl24.xml.bak`. I left everything as it was.
- nhl_game: 2 copies, byte-identical. I didn't finish reading initial_experience (the read was slow, so I stopped it).

## 2. Defaults swap code (committed efb9b74 + 828577b; 2-line uncommitted fix in KitDefaults.cs that clones the bytes before load)
- **Bug for this build:** `KitDefaults` assumes each team's current defaults are variants 0/1. On D:, 9 teams have their defaults elsewhere: Boston 6/7, Buffalo 4/5, Carolina 2/3, Edmonton 4/5, Ottawa 3/4, Philadelphia 0/1 + 6/7, San Jose 4/5, Vancouver 4/5, Vegas 1/2. Running it as-is would leave two defaults per side on those teams. It would also pick up "World of CHEL Tier 1" (33 teams qualify, not 32). It needs fixing before any test run: clear whichever rows are default now, per dark/light, and limit it to the 32 NHL teams.

## 3. Locstring codes (from eboot-v32 + eng_us)
- The jersey-name table in the eboot runs: 2 ALTERNATEJERSEY ("Alternate"), 3 OLD_HOME ("2006-07 Home"), 4 OLD_AWAY ("2006-07 Away"), 5 OLD_2NDHOME ("2006-07 2nd Home"), 6 VINTAGE_%d_%d_%d (per kit), then TXT_CHL_HOME / TXT_CHL_AWAY, which are codes 8 and 9 in the data. 0/1 are Home/Away.
- **Code 7:** no string sits between VINTAGE and CHL_HOME, and neither game uses 7 in any jersey row. So it's either a blank gap or handled in code. I can't tell whether it's a single shared key without disassembling `CreateStockTeamJerseyChoices` or doing an in-game test on a copy.
- The 3/4 and 8/9 keys are shared, but the 2006-07 Reebok kits (29/29) and the European CHL kits (24/24) already use them. Renaming them to "23-24 Adidas" would mislabel those kits.
- English table: 65,498 of 65,535 entries used, so **37 free**. The per-kit "23-24 Adidas" names need 64.

## 4. Capacity (live nhl_game)
- exhibitionteams 531/577 (46 spare rows). stockteamjerseys 2342/2799 (457 spare). stockteamequipmenttable 9336/12781. stockleagues 33/33 (full: no new leagues, so no PWHL league).
- cas_05: 3,690,459,199 bytes now. The hard cap is 4 GiB, which leaves about 604 MB, or about 45 kits at about 13.3 MB per kit (0.85 GB measured for the 64-kit install). Every clone appends to cas_05 (Anaheim donor).

## 5. Inventory totals (real leagues; EASHL/HUT/Pro-Am left out)
- Teams matched by name: 451 rows across every league NHL 24 has.
- NHL 27 uniform slots missing on matched teams: **84**. That's NHL 46, AHL 3, ECHL 1, OHL 2, QMJHL 1, WHL 4, and Europe 27 (SHL 4, Liiga 4, DEL 4, Extraliga 7, Swiss 4, ICEHL 4). 23 of the 84 need their own name entry (loc6), and 1 uses NHL 27's code 12, which NHL 24 doesn't have.
- NHL 27 teams that don't exist in NHL 24: **46**, carrying **97 kits**. 7 of them sit in a team slot NHL 24 leaves unused (96 Tahoe, 97 Greensboro, 205 Kiekko-Espoo, 248 Milano, 262 Nitra, 269 Mannheim-Spengler, 379 Norway W). The other 39 take over a slot that NHL 24 gives to another team: Bridgeport becomes Hamilton Hammers (AHL), Mississauga becomes Brampton (OHL), Acadie-Bathurst becomes Newfoundland (QMJHL), Top Prospects Red becomes Penticton (WHL), the 12 Women's NT teams become the 12 PWHL teams, and so on. Arizona to Utah was done the same way.
- Full kit count if everything were ported: 84 + 97 = **about 181 kits, about 2.4 GB in cas_05**. That's well past the about 604 MB of headroom, so the CAS spill/reclaim fix has to come first. Even the 84 matched-team kits alone (about 1.1 GB) don't fit.
- Unproven: the pipeline has only cloned from the NHL adidas donor (style 2). AHL/CHL kits are style 1 and the European ones style 0, so the donor choice and garment templates for those styles are untested.
- Same-slot kits whose art NHL 27 redrew aren't counted. That needs a texture compare, team by team.

## 6. Fresh-copy test
- D: has 522 GB free. A full copy needs about 50 GB (stock-restore base + update-UPDATE plus the small NHL24PC files). `D:\CIMS-test` has only `release`. The `stock-v32` baseline the handoff mentions is no longer there.

---
# NHL 27 to NHL 24 uniform inventory (read-only, 2026-11-21)

Sources: live D:/NHL24PC_V3.2 nhl_game (stockteamjerseys, exhibitionteams) vs D:/dev/NHL27Convert/reference/nhl27-full-csv. Active jersey rows only. "missing" = an NHL 27 style/variant slot with no NHL 24 row for that team. NHL Fanatics home/away already installed (24/25) are excluded.

Caveat: a slot present in both games counts as matched even if NHL 27 redrew the art in it. That needs a texture-level compare, which I have not done.

Name codes: loc0 Home, loc1 Away, loc2 Alternate, loc8/9 CHL Home/Away (shared keys, no new text needed); loc6 = per-kit VINTAGE name (needs 1 new English entry each); loc12 = NHL 27-only code.

## NHL  (matched 36, NHL 27-only 0, missing kits on matched 46)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 0 | Anaheim Ducks | 19 | 19 | - |
| 1 | Winnipeg Jets | 13 | 14 | 2/12 loc2 |
| 2 | Boston Bruins | 21 | 24 | 2/10 loc6, 2/11 loc6 light, 2/15 loc6 |
| 3 | Buffalo Sabres | 19 | 20 | 2/12 loc2 |
| 4 | Calgary Flames | 18 | 19 | 2/12 loc6 |
| 5 | Carolina Hurricanes | 20 | 22 | 2/11 loc6 light, 2/12 loc2 |
| 6 | Chicago Blackhawks | 19 | 22 | 2/10 loc0 DEF, 2/12 loc2, 2/13 loc6 |
| 7 | Colorado Avalanche | 18 | 20 | 2/12 loc2, 2/13 loc6 |
| 8 | Columbus Blue Jackets | 13 | 15 | 2/12 loc2, 2/13 loc6 |
| 9 | Dallas Stars | 21 | 23 | 2/12 loc6, 2/13 loc2 |
| 10 | Detroit Red Wings | 15 | 17 | 2/12 loc6, 2/13 loc6 light |
| 11 | Edmonton Oilers | 18 | 20 | 2/12 loc6, 2/13 loc2 light |
| 12 | Florida Panthers | 15 | 16 | 2/13 loc6 |
| 13 | Los Angeles Kings | 23 | 24 | 2/12 loc2 |
| 14 | Minnesota Wild | 16 | 18 | 2/12 loc2, 2/13 loc6 light |
| 15 | Montreal Canadiens | 14 | 14 | - |
| 16 | Nashville Predators | 16 | 16 | - |
| 17 | New Jersey Devils | 19 | 20 | 2/12 loc6 |
| 18 | New York Islanders | 19 | 19 | - |
| 19 | New York Rangers | 19 | 22 | 2/12 loc2, 2/13 loc6, 2/14 loc6 light |
| 20 | Ottawa Senators | 17 | 18 | 2/12 loc2 |
| 21 | Philadelphia Flyers | 20 | 21 | 2/12 loc2 |
| 22 | Utah Mammoth | 18 | 2 | - |
| 23 | Pittsburgh Penguins | 22 | 24 | 2/12 loc6, 2/13 loc2 |
| 24 | St. Louis Blues | 19 | 22 | 2/10 loc2, 2/11 loc6 light, 2/13 loc6 light |
| 25 | San Jose Sharks | 20 | 22 | 2/12 loc2, 2/13 loc6 |
| 26 | Tampa Bay Lightning | 15 | 18 | 2/4 loc12, 2/12 loc6, 2/13 loc6 |
| 27 | Toronto Maple Leafs | 26 | 27 | 2/12 loc2 |
| 28 | Vancouver Canucks | 23 | 24 | 2/12 loc2 |
| 29 | Washington Capitals | 17 | 18 | 2/12 loc2 |
| 30 | Vegas Golden Knights | 9 | 10 | 2/12 loc2 |
| 31 | Seattle Kraken | 6 | 7 | 2/12 loc2 |
| 32 | Atlantic Division All-Stars | 16 | 16 | - |
| 33 | Central Division All-Stars | 16 | 16 | - |
| 34 | Metropolitan Division All-Stars | 16 | 16 | - |
| 35 | Pacific Division All-Stars | 16 | 16 | - |

## AHL  (matched 31, NHL 27-only 1, missing kits on matched 3)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 36 | Abbotsford Canucks | 2 | 2 | - |
| 37 | Bakersfield Condors | 3 | 3 | - |
| 38 | Belleville Senators | 3 | 3 | - |
| 40 | Charlotte Checkers | 3 | 3 | - |
| 41 | Chicago Wolves | 3 | 3 | - |
| 42 | Cleveland Monsters | 3 | 3 | - |
| 43 | Colorado Eagles | 3 | 3 | - |
| 44 | Grand Rapids Griffins | 2 | 3 | 1/2 loc2 |
| 45 | Hartford Wolf Pack | 3 | 3 | - |
| 46 | Henderson Silver Knights | 3 | 3 | - |
| 47 | Hershey Bears | 3 | 3 | - |
| 48 | Iowa Wild | 3 | 3 | - |
| 49 | Laval Rocket | 2 | 2 | - |
| 50 | Lehigh Valley Phantoms | 2 | 2 | - |
| 51 | Manitoba Moose | 2 | 2 | - |
| 52 | Milwaukee Admirals | 3 | 3 | - |
| 53 | Ontario Reign | 2 | 2 | - |
| 54 | Providence Bruins | 3 | 3 | - |
| 55 | Rochester Americans | 2 | 2 | - |
| 56 | Rockford IceHogs | 3 | 3 | - |
| 57 | San Diego Gulls | 3 | 3 | - |
| 58 | San Jose Barracuda | 3 | 3 | - |
| 59 | Springfield Thunderbirds | 3 | 3 | - |
| 60 | Calgary Wranglers | 3 | 3 | - |
| 61 | Syracuse Crunch | 3 | 3 | - |
| 62 | Texas Stars | 2 | 3 | 1/2 loc2 |
| 63 | Toronto Marlies | 2 | 2 | - |
| 64 | Tucson Roadrunners | 3 | 3 | - |
| 65 | Utica Comets | 3 | 3 | - |
| 66 | Wilkes-Barre/Scranton Penguins | 3 | 3 | - |
| 67 | Coachella Valley Firebirds | 2 | 3 | 1/2 loc2 |
| 39 | **NEW: Hamilton Hammers** | - | 2 | 24 slot 39 = Bridgeport Islanders (AHL) |

## TXT_ECHL  (matched 25, NHL 27-only 5, missing kits on matched 1)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 68 | Adirondack Thunder | 3 | 3 | - |
| 69 | Allen Americans | 3 | 3 | - |
| 70 | Atlanta Gladiators | 3 | 2 | - |
| 71 | Cincinnati Cyclones | 3 | 3 | - |
| 72 | Florida Everblades | 2 | 2 | - |
| 73 | Fort Wayne Komets | 3 | 3 | - |
| 74 | Greenville Swamp Rabbits | 3 | 3 | - |
| 75 | Idaho Steelheads | 3 | 3 | - |
| 76 | Indy Fuel | 3 | 3 | - |
| 78 | Jacksonville IceMen | 3 | 3 | - |
| 79 | Kalamazoo Wings | 3 | 2 | - |
| 80 | Kansas City Mavericks | 3 | 3 | - |
| 81 | Maine Mariners | 3 | 3 | - |
| 83 | Norfolk Admirals | 3 | 2 | - |
| 84 | Orlando Solar Bears | 3 | 3 | - |
| 85 | Rapid City Rush | 3 | 3 | - |
| 86 | Reading Royals | 3 | 2 | - |
| 87 | South Carolina Stingrays | 3 | 3 | - |
| 88 | Toledo Walleye | 4 | 4 | - |
| 89 | Trois-Rivieres Lions | 2 | 3 | 1/2 loc2 |
| 90 | Tulsa Oilers | 4 | 4 | - |
| 92 | Wheeling Nailers | 3 | 3 | - |
| 93 | Wichita Thunder | 3 | 3 | - |
| 94 | Worcester Railers | 3 | 3 | - |
| 95 | Savannah Ghost Pirates | 3 | 3 | - |
| 77 | **NEW: New Mexico Goatheads** | - | 2 | 24 slot 77 = Iowa Heartlanders (TXT_ECHL) |
| 82 | **NEW: Bloomington Bison** | - | 3 | 24 slot 82 = Newfoundland Growlers (TXT_ECHL) |
| 91 | **NEW: Trenton Ironhawks** | - | 2 | 24 slot 91 = Utah Grizzlies (TXT_ECHL) |
| 96 | **NEW: Tahoe Knight Monsters** | - | 3 | 24 slot 96 unused |
| 97 | **NEW: Greensboro Gargoyles** | - | 3 | 24 slot 97 unused |

## OHL  (matched 19, NHL 27-only 1, missing kits on matched 2)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 98 | Barrie Colts | 2 | 2 | - |
| 99 | Erie Otters | 3 | 3 | - |
| 100 | Flint Firebirds | 2 | 3 | 1/2 loc2 |
| 101 | Guelph Storm | 2 | 3 | 1/2 loc2 |
| 102 | Brantford Bulldogs | 3 | 3 | - |
| 103 | Kingston Frontenacs | 3 | 3 | - |
| 104 | Kitchener Rangers | 3 | 3 | - |
| 105 | London Knights | 3 | 3 | - |
| 107 | Niagara IceDogs | 3 | 3 | - |
| 108 | North Bay Battalion | 4 | 4 | - |
| 109 | Oshawa Generals | 3 | 3 | - |
| 110 | Ottawa 67's | 2 | 2 | - |
| 111 | Owen Sound Attack | 3 | 3 | - |
| 112 | Peterborough Petes | 3 | 3 | - |
| 113 | Saginaw Spirit | 2 | 2 | - |
| 114 | Sarnia Sting | 3 | 3 | - |
| 115 | Sault Ste. Marie Greyhounds | 3 | 3 | - |
| 116 | Sudbury Wolves | 3 | 3 | - |
| 117 | Windsor Spitfires | 3 | 3 | - |
| 106 | **NEW: Brampton Steelheads** | - | 3 | 24 slot 106 = Mississauga Steelheads (OHL) |

## QMJHL  (matched 17, NHL 27-only 1, missing kits on matched 1)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 119 | Baie-Comeau Drakkar | 3 | 2 | - |
| 120 | Blainville-Boisbriand Armada | 3 | 2 | - |
| 121 | Cape Breton Eagles | 3 | 3 | - |
| 122 | Charlottetown Islanders | 3 | 3 | - |
| 123 | Chicoutimi Saguenéens | 3 | 4 | 1/3 loc2 |
| 124 | Drummondville Voltigeurs | 3 | 3 | - |
| 125 | Gatineau Olympiques | 3 | 3 | - |
| 126 | Halifax Mooseheads | 3 | 3 | - |
| 127 | Moncton Wildcats | 4 | 3 | - |
| 128 | Québec Remparts | 4 | 3 | - |
| 129 | Rimouski Océanic | 2 | 2 | - |
| 130 | Rouyn-Noranda Huskies | 3 | 2 | - |
| 131 | Saint John Sea Dogs | 3 | 2 | - |
| 132 | Shawinigan Cataractes | 3 | 3 | - |
| 133 | Sherbrooke Phoenix | 2 | 2 | - |
| 134 | Val-D'Or Foreurs | 2 | 2 | - |
| 135 | Victoriaville Tigres | 2 | 2 | - |
| 118 | **NEW: Newfoundland Regiment** | - | 2 | 24 slot 118 = Acadie-Bathurst Titan (QMJHL) |

## WHL  (matched 22, NHL 27-only 1, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 136 | Brandon Wheat Kings | 3 | 3 | - |
| 137 | Calgary Hitmen | 3 | 3 | - |
| 138 | Edmonton Oil Kings | 3 | 3 | - |
| 139 | Everett Silvertips | 2 | 3 | 1/2 loc2 |
| 140 | Kamloops Blazers | 3 | 3 | - |
| 141 | Kelowna Rockets | 3 | 3 | - |
| 142 | Lethbridge Hurricanes | 3 | 3 | - |
| 143 | Medicine Hat Tigers | 2 | 2 | - |
| 144 | Moose Jaw Warriors | 3 | 3 | - |
| 145 | Portland Winter Hawks | 3 | 3 | - |
| 146 | Prince Albert Raiders | 3 | 3 | - |
| 147 | Prince George Cougars | 3 | 2 | - |
| 148 | Red Deer Rebels | 4 | 4 | - |
| 149 | Regina Pats | 3 | 4 | 1/3 loc2 light |
| 150 | Saskatoon Blades | 3 | 2 | - |
| 151 | Seattle Thunderbirds | 2 | 3 | 1/2 loc2 |
| 152 | Spokane Chiefs | 3 | 3 | - |
| 153 | Swift Current Broncos | 3 | 2 | - |
| 154 | Tri-City Americans | 4 | 2 | - |
| 155 | Vancouver Giants | 4 | 3 | - |
| 156 | Victoria Royals | 3 | 3 | - |
| 157 | Wenatchee Wild | 2 | 3 | 1/2 loc2 |
| 158 | **NEW: Penticton Vees** | - | 3 | 24 slot 158 = Top Prospects Red (PROSPECT_TEAMS) |

## elitserien  (matched 14, NHL 27-only 0, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 175 (24: 189) | Växjö Lakers | 4 | 4 | - |
| 176 (24: 165) | Djurgården Hockey (NHL 24 league: Allsvenskan) | 3 | 2 | - |
| 177 | HV71 | 3 | 2 | - |
| 178 | Färjestad BK | 4 | 2 | - |
| 179 | Frölunda HC | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 180 (24: 164) | IF Bjorkloven (NHL 24 league: Allsvenskan) | 2 | 2 | - |
| 181 | Linköping HC | 2 | 2 | - |
| 182 | Luleå Hockey | 2 | 2 | - |
| 183 | Malmӧ Redhawks | 2 | 2 | - |
| 184 | Orebro Hockey | 3 | 2 | - |
| 185 (24: 168) | Brynäs IF (NHL 24 league: Allsvenskan) | 3 | 2 | - |
| 186 | Rogle BK | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 187 | Skelleftea AIK | 4 | 4 | - |
| 188 | Timrå IK | 2 | 2 | - |

## Allsvenskan  (matched 12, NHL 27-only 2, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 161 (24: 175) | Nybro Vikings IF | 2 | 2 | - |
| 162 | AIK | 2 | 2 | - |
| 163 | Almtuna IS | 2 | 2 | - |
| 164 (24: 180) | Leksands IF (NHL 24 league: elitserien) | 2 | 2 | - |
| 165 (24: 176) | MODO Hockey (NHL 24 league: elitserien) | 2 | 2 | - |
| 166 | BIK Karlskoga | 2 | 2 | - |
| 167 | Kalmar HC | 2 | 2 | - |
| 168 (24: 185) | IK Oskarshamn (NHL 24 league: elitserien) | 2 | 2 | - |
| 169 | Mora IK | 2 | 2 | - |
| 170 | Södertälje SK | 2 | 2 | - |
| 172 | Ostersunds IK | 2 | 2 | - |
| 173 | Vik Vasteras HK | 2 | 2 | - |
| 171 | **NEW: Visby/Roma HK** | - | 2 | 24 slot 171 = Tingsryds AIF (Allsvenskan) |
| 174 | **NEW: Vimmerby Hockey** | - | 2 | 24 slot 174 = Vasterviks IK (Allsvenskan) |

## SMLIIGA  (matched 15, NHL 27-only 2, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 190 | Porin Ässät | 3 | 2 | - |
| 191 | Helsingin IFK | 2 | 2 | - |
| 192 | HPK Hämeenlinna | 2 | 2 | - |
| 193 | Tampereen Ilves | 5 | 2 | - |
| 194 | Mikkelin Jukurit | 2 | 2 | - |
| 195 | JYP Jyväskylä | 2 | 2 | - |
| 196 | KalPa Kuopio | 2 | 2 | - |
| 197 | Oulun Kärpät | 3 | 2 | - |
| 198 | KooKoo Kouvala | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 199 | Rauman Lukko | 4 | 2 | - |
| 200 | Pelicans Lahti | 5 | 2 | - |
| 201 | SaiPa Lappeenranta | 2 | 4 | 0/10 loc8 light, 0/11 loc9 |
| 202 | Vaasan Sport | 2 | 2 | - |
| 203 | Tampereen Tappara | 5 | 4 | - |
| 204 | TPS Turku | 2 | 2 | - |
| 189 | **NEW: Jokerit Helsinki** | - | 2 | 24 slot 189 = Växjö Lakers (elitserien) |
| 205 | **NEW: Kiekko-Espoo** | - | 2 | 24 slot 205 unused |

## TXT_LEAGUE_DEL  (matched 13, NHL 27-only 1, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 206 | Augsburger Panther | 3 | 2 | - |
| 208 | Eisbären Berlin | 3 | 4 | 0/10 loc8, 0/11 loc9 light |
| 209 | Fischtown Pinguins | 3 | 2 | - |
| 210 | ERC Ingolstadt | 5 | 2 | - |
| 211 | Iserlohn Roosters | 3 | 2 | - |
| 212 | Kölner Haie | 3 | 4 | 0/10 loc8, 0/11 loc9 light |
| 213 | Lowen Frankfurt | 3 | 2 | - |
| 214 | Adler Mannheim | 5 | 4 | - |
| 215 | EHC München | 5 | 2 | - |
| 216 | Nurnberg Ice Tigers | 3 | 2 | - |
| 217 | Schwenninger Wild Wings | 3 | 2 | - |
| 218 | Straubing Tigers | 3 | 2 | - |
| 219 | Grizzly Adams Wolfsburg | 3 | 2 | - |
| 207 | **NEW: Krefeld Pinguine** | - | 2 | 24 slot 207 = Düsseldorfer EG (TXT_LEAGUE_DEL) |

## TXT_LEAGUE_O2_EXTRALIGA  (matched 14, NHL 27-only 0, missing kits on matched 7)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 220 | HC Kometa Brno | 2 | 2 | - |
| 221 | CEZ Motor Ceske Budejovice | 2 | 2 | - |
| 222 | HC Energie Karlovy Vary | 2 | 2 | - |
| 223 | Rytíři Kladno | 2 | 2 | - |
| 224 | Bilí Tygři Liberec | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 225 | HC VERVA Litvinov | 2 | 2 | - |
| 226 | BK Mladá Boleslav | 2 | 2 | - |
| 227 | Mountfield HK | 2 | 2 | - |
| 228 | HC Olomouc | 2 | 3 | 0/2 loc2 |
| 229 | HC Dynamo Pardubice | 4 | 5 | 0/2 loc2 |
| 230 | HC SKODA Plzeň | 2 | 4 | 0/10 loc8 light, 0/11 loc9 |
| 231 | HC Sparta Praha | 2 | 3 | 0/2 loc2 |
| 232 | HC Oceláři Třinec | 4 | 2 | - |
| 233 | HC Vítkovice Ridera | 4 | 2 | - |

## TXT_LEAGUE_SWISS_LEAGUE  (matched 14, NHL 27-only 0, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 234 | Kloten Flyers | 2 | 2 | - |
| 235 | HC Ajoie | 2 | 2 | - |
| 236 | HC Ambri-Piotta | 2 | 2 | - |
| 237 | SC Bern | 2 | 2 | - |
| 238 | EHC Biel | 4 | 2 | - |
| 239 | HC Davos | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 240 | HC Fribourg-Gottéron | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 241 | Genève-Servette HC | 4 | 4 | - |
| 242 | HC Lausanne | 2 | 2 | - |
| 243 | HC Lugano | 2 | 2 | - |
| 244 | Rapperswil-Jona Lakers | 4 | 2 | - |
| 245 | SCL Tigers | 2 | 2 | - |
| 246 | ZSC Lions | 2 | 2 | - |
| 247 | EV Zug | 2 | 2 | - |

## TXT_LEAGUE_EBEL  (matched 10, NHL 27-only 4, missing kits on matched 4)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 249 | HC Bolzano Sudtirol Alpheria | 4 | 2 | - |
| 250 | Black Wings Linz | 2 | 2 | - |
| 251 | Pioneers Vorarlberg | 2 | 2 | - |
| 252 | HYDRO Fehervar AV 19 | 2 | 2 | - |
| 253 | Graz 99ers | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 254 | HC TWK Innsbruck | 4 | 2 | - |
| 255 | EC KAC | 2 | 4 | 0/10 loc8, 0/11 loc9 light |
| 258 | Red Bull Salzburg | 4 | 4 | - |
| 259 | Vienna Capitals | 2 | 2 | - |
| 260 | EC iDM Warmepumpen VSV | 2 | 2 | - |
| 248 | **NEW: Hockey Club Milano** | - | 2 | 24 slot 248 unused |
| 256 | **NEW: Olimpija Ljubljana** | - | 2 | 24 slot 256 = HK SZ Olimpija (TXT_LEAGUE_EBEL) |
| 257 | **NEW: HC Falkensteiner Pustertal Wolfe** | - | 2 | 24 slot 257 = HC Pustertal Wolfe (TXT_LEAGUE_EBEL) |
| 261 | **NEW: FTC Telecom Budapest** | - | 2 | 24 slot 261 = Asiago Hockey (TXT_LEAGUE_EBEL) |

## TXT_CHAMPIONS_LEAGUE  (matched 0, NHL 27-only 5, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 262 | **NEW: HK Nitra** | - | 2 | 24 slot 262 unused |
| 263 | **NEW: Herning Blue Fox** | - | 2 | 24 slot 263 = Aalborg Pirates (TXT_CHAMPIONS_LEAGUE) |
| 264 | **NEW: Bordeaux Boxers** | - | 2 | 24 slot 264 = Rouen Dragons (TXT_CHAMPIONS_LEAGUE) |
| 265 | **NEW: Storhamar Hamar** | - | 2 | 24 slot 265 = Stavanger Oilers (TXT_CHAMPIONS_LEAGUE) |
| 266 | **NEW: GKS Tychy** | - | 2 | 24 slot 266 = HC Košice (TXT_CHAMPIONS_LEAGUE) |

## TXT_SPENGLER_CUP  (matched 2, NHL 27-only 4, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 267 (24: 270) | HC Davos - Spengler | 2 | 2 | - |
| 268 (24: 273) | Frölunda HC - Spengler | 2 | 2 | - |
| 269 | **NEW: Adler Mannheim - Spengler** | - | 2 | 24 slot 269 unused |
| 270 | **NEW: US College Selects - Spengler** | - | 2 | 24 slot 270 = HC Davos - Spengler (TXT_SPENGLER_CUP) |
| 271 | **NEW: Tampereen Ilves - Spengler** | - | 2 | 24 slot 271 = Canada - Spengler (TXT_SPENGLER_CUP) |
| 272 | **NEW: SCL Tigers - Spengler** | - | 2 | 24 slot 272 = HC Dynamo Pardubice - Spengler (TXT_SPENGLER_CUP) |

## TXT_LEAGUE_NHL_ALUMNI  (matched 32, NHL 27-only 1, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 278 | Anaheim Ducks Alumni | 10 | 10 | - |
| 280 | Boston Bruins Alumni | 9 | 9 | - |
| 281 | Buffalo Sabres Alumni | 7 | 7 | - |
| 282 | Calgary Flames Alumni | 7 | 7 | - |
| 283 | Carolina Hurricanes Alumni | 7 | 7 | - |
| 284 | Chicago Blackhawks Alumni | 11 | 11 | - |
| 285 | Colorado Avalanche Alumni | 8 | 8 | - |
| 286 | Columbus Blue Jackets Alumni | 6 | 6 | - |
| 287 | Dallas Stars Alumni | 6 | 6 | - |
| 288 | Detroit Red Wings Alumni | 9 | 9 | - |
| 289 | Edmonton Oilers Alumni | 8 | 8 | - |
| 290 | Florida Panthers Alumni | 9 | 9 | - |
| 291 | Los Angeles Kings Alumni | 13 | 13 | - |
| 292 | Minnesota Wild Alumni | 7 | 7 | - |
| 293 | Montreal Canadiens Alumni | 7 | 7 | - |
| 294 | Nashville Predators Alumni | 8 | 8 | - |
| 295 | New Jersey Devils Alumni | 6 | 6 | - |
| 296 | New York Islanders Alumni | 11 | 11 | - |
| 297 | New York Rangers Alumni | 10 | 10 | - |
| 298 | Ottawa Senators Alumni | 8 | 8 | - |
| 299 | Philadelphia Flyers Alumni | 9 | 9 | - |
| 300 | Pittsburgh Penguins Alumni | 12 | 12 | - |
| 301 | St. Louis Blues Alumni | 10 | 10 | - |
| 302 | San Jose Sharks Alumni | 9 | 9 | - |
| 303 | Tampa Bay Lightning Alumni | 7 | 7 | - |
| 304 | Toronto Maple Leafs Alumni | 12 | 12 | - |
| 305 | Vancouver Canucks Alumni | 12 | 12 | - |
| 306 | Washington Capitals Alumni | 7 | 7 | - |
| 307 | Winnipeg Jets Alumni | 5 | 5 | - |
| 308 | Hartford Whalers Alumni | 2 | 2 | - |
| 309 | Minnesota North Stars Alumni | 2 | 2 | - |
| 310 | Quebec Nordiques Alumni | 2 | 2 | - |
| 279 | **NEW: Vegas Golden Knights Alumni** | - | 2 | 24 slot 279 = Arizona Coyotes Alumni (TXT_LEAGUE_NHL_ALUMNI) |

## TXT_WORLD_JUNIORS  (matched 9, NHL 27-only 1, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 342 | Canada - Mens Juniors | 3 | 2 | - |
| 343 | Czech Republic - Mens Juniors | 2 | 2 | - |
| 344 | Finland - Mens Juniors | 2 | 2 | - |
| 345 | Germany - Mens Juniors | 2 | 2 | - |
| 346 | Latvia - Mens Juniors | 2 | 2 | - |
| 347 | Slovakia - Mens Juniors | 2 | 2 | - |
| 348 | Sweden - Mens Juniors | 2 | 2 | - |
| 349 | Switzerland - Mens Juniors | 2 | 2 | - |
| 350 | USA - Mens Juniors | 3 | 2 | - |
| 341 | **NEW: Denmark - Mens Juniors** | - | 2 | 24 slot 341 = Norway - Mens Juniors (TXT_WORLD_JUNIORS) |

## TXT_WORLD_CHAMPIONSHIPS  (matched 13, NHL 27-only 3, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 351 | Canada - Mens Worlds | 3 | 2 | - |
| 352 | Austria - Mens Worlds | 2 | 2 | - |
| 353 | Czech Republic - Mens Worlds | 2 | 2 | - |
| 354 | Denmark - Mens Worlds | 2 | 2 | - |
| 355 (24: 357) | Great Britain - Mens Worlds | 2 | 2 | - |
| 356 | Germany - Mens Worlds | 2 | 2 | - |
| 359 | Finland - Mens Worlds | 2 | 2 | - |
| 360 | Latvia - Mens Worlds | 2 | 2 | - |
| 361 | Norway - Mens Worlds | 2 | 2 | - |
| 363 | Sweden - Mens Worlds | 2 | 2 | - |
| 364 | Slovakia - Mens Worlds | 2 | 2 | - |
| 365 | USA - Mens Worlds | 3 | 2 | - |
| 366 | Switzerland - Mens Worlds | 2 | 2 | - |
| 357 | **NEW: Hungary - Mens Worlds** | - | 2 | 24 slot 357 = Great Britain - Mens Worlds (TXT_WORLD_CHAMPIONSHIPS) |
| 358 | **NEW: Slovenia - Mens Worlds** | - | 2 | 24 slot 358 = Poland - Mens Worlds (TXT_WORLD_CHAMPIONSHIPS) |
| 362 | **NEW: Italy - Mens Worlds** | - | 2 | 24 slot 362 = France - Mens Worlds (TXT_WORLD_CHAMPIONSHIPS) |

## TXT_W_WORLD_CHAMPIONSHIPS  (matched 12, NHL 27-only 2, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 367 | Canada - Womens Worlds | 3 | 2 | - |
| 368 | Czech Republic - Womens Worlds | 2 | 2 | - |
| 370 | Finland - Womens Worlds | 2 | 2 | - |
| 371 | Germany - Womens Worlds | 2 | 2 | - |
| 372 | Hungary - Womens Worlds | 2 | 2 | - |
| 373 | Japan - Womens Worlds | 2 | 2 | - |
| 374 | Sweden - Womens Worlds | 2 | 2 | - |
| 375 | Switzerland - Womens Worlds | 2 | 2 | - |
| 376 | USA - Womens Worlds | 3 | 2 | - |
| 377 | China - Womens Worlds | 2 | 2 | - |
| 378 | Denmark - Womens Worlds | 2 | 2 | - |
| 380 (24: 369) | France - Womens Worlds | 2 | 2 | - |
| 369 | **NEW: Austria - Womens Worlds** | - | 2 | 24 slot 369 = France - Womens Worlds (TXT_W_WORLD_CHAMPIONSHIPS) |
| 379 | **NEW: Norway - Womens Worlds** | - | 2 | 24 slot 379 unused |

## TXT_INTERNATIONAL  (matched 24, NHL 27-only 0, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 317 | Austria | 2 | 2 | - |
| 318 | Belarus | 2 | 2 | - |
| 319 | Canada | 4 | 2 | - |
| 320 | Czech Republic | 2 | 2 | - |
| 321 | Denmark | 2 | 2 | - |
| 322 | Finland | 2 | 2 | - |
| 323 | France | 2 | 2 | - |
| 324 | Germany | 2 | 2 | - |
| 325 | Great Britain | 2 | 2 | - |
| 326 | Italy | 2 | 2 | - |
| 327 | Japan | 2 | 2 | - |
| 328 | Kazakstan | 2 | 2 | - |
| 329 | Latvia | 2 | 2 | - |
| 330 | Norway | 2 | 2 | - |
| 331 | Poland | 2 | 2 | - |
| 332 | Russia | 2 | 2 | - |
| 333 | Slovakia | 2 | 2 | - |
| 334 | Sweden | 2 | 2 | - |
| 335 | Switzerland | 2 | 2 | - |
| 336 | Ukraine | 2 | 2 | - |
| 337 | USA | 3 | 2 | - |
| 338 | Hungary | 2 | 2 | - |
| 339 | Slovenia | 2 | 2 | - |
| 340 | South Korea | 2 | 2 | - |

## TXT_PWHL  (matched 0, NHL 27-only 12, missing kits on matched 0)

| slot | team | 24 kits | 27 kits | missing (style/variant, name code) / new-team note |
|---|---|---|---|---|
| 381 | **NEW: Minnesota Frost** | - | 2 | 24 slot 381 = Canada - Womens NT (TXT_W_INTERNATIONAL) |
| 382 | **NEW: Montreal Victoire** | - | 2 | 24 slot 382 = Czech Republic - Womens NT (TXT_W_INTERNATIONAL) |
| 383 | **NEW: New York Sirens** | - | 2 | 24 slot 383 = France - Womens NT (TXT_W_INTERNATIONAL) |
| 384 | **NEW: Ottawa Charge** | - | 2 | 24 slot 384 = Finland - Womens NT (TXT_W_INTERNATIONAL) |
| 385 | **NEW: Toronto Sceptres** | - | 2 | 24 slot 385 = Germany - Womens NT (TXT_W_INTERNATIONAL) |
| 386 | **NEW: Vancouver Goldeneyes** | - | 2 | 24 slot 386 = Hungary - Womens NT (TXT_W_INTERNATIONAL) |
| 387 | **NEW: Seattle Torrent** | - | 2 | 24 slot 387 = Japan - Womens NT (TXT_W_INTERNATIONAL) |
| 388 | **NEW: Boston Fleet** | - | 2 | 24 slot 388 = Sweden - Womens NT (TXT_W_INTERNATIONAL) |
| 389 | **NEW: PWHL Detroit** | - | 2 | 24 slot 389 = Switzerland - Womens NT (TXT_W_INTERNATIONAL) |
| 390 | **NEW: PWHL Las Vegas** | - | 2 | 24 slot 390 = USA - Womens NT (TXT_W_INTERNATIONAL) |
| 391 | **NEW: PWHL Hamilton** | - | 2 | 24 slot 391 = China - Womens NT (TXT_W_INTERNATIONAL) |
| 392 | **NEW: PWHL San Jose** | - | 2 | 24 slot 392 = Denmark - Womens NT (TXT_W_INTERNATIONAL) |

## NHL 24 teams with no NHL 27 counterpart by name

- AHL slot 39: Bridgeport Islanders
- TXT_ECHL slot 77: Iowa Heartlanders
- TXT_ECHL slot 82: Newfoundland Growlers
- TXT_ECHL slot 91: Utah Grizzlies
- OHL slot 106: Mississauga Steelheads
- QMJHL slot 118: Acadie-Bathurst Titan
- PROSPECT_TEAMS slot 158: Top Prospects Red
- PROSPECT_TEAMS slot 159: Top Prospects White
- Allsvenskan slot 171: Tingsryds AIF
- Allsvenskan slot 174: Vasterviks IK
- TXT_LEAGUE_DEL slot 207: Düsseldorfer EG
- TXT_LEAGUE_EBEL slot 256: HK SZ Olimpija
- TXT_LEAGUE_EBEL slot 257: HC Pustertal Wolfe
- TXT_LEAGUE_EBEL slot 261: Asiago Hockey
- TXT_CHAMPIONS_LEAGUE slot 263: Aalborg Pirates
- TXT_CHAMPIONS_LEAGUE slot 264: Rouen Dragons
- TXT_CHAMPIONS_LEAGUE slot 265: Stavanger Oilers
- TXT_CHAMPIONS_LEAGUE slot 266: HC Košice
- TXT_CHAMPIONS_LEAGUE slot 267: Belfast Giants
- TXT_SPENGLER_CUP slot 271: Canada - Spengler
- TXT_SPENGLER_CUP slot 272: HC Dynamo Pardubice - Spengler
- TXT_SPENGLER_CUP slot 274: KalPa Kuopio - Spengler
- TXT_SPENGLER_CUP slot 275: HC Ambri-Piotta - Spengler
- TXT_LEAGUE_NHL_ALUMNI slot 279: Arizona Coyotes Alumni
- TXT_WORLD_JUNIORS slot 341: Norway - Mens Juniors
- TXT_WORLD_CHAMPIONSHIPS slot 355: Kazakstan - Mens Worlds
- TXT_WORLD_CHAMPIONSHIPS slot 358: Poland - Mens Worlds
- TXT_WORLD_CHAMPIONSHIPS slot 362: France - Mens Worlds
- TXT_W_INTERNATIONAL slot 381: Canada - Womens NT
- TXT_W_INTERNATIONAL slot 382: Czech Republic - Womens NT
- TXT_W_INTERNATIONAL slot 383: France - Womens NT
- TXT_W_INTERNATIONAL slot 384: Finland - Womens NT
- TXT_W_INTERNATIONAL slot 385: Germany - Womens NT
- TXT_W_INTERNATIONAL slot 386: Hungary - Womens NT
- TXT_W_INTERNATIONAL slot 387: Japan - Womens NT
- TXT_W_INTERNATIONAL slot 388: Sweden - Womens NT
- TXT_W_INTERNATIONAL slot 389: Switzerland - Womens NT
- TXT_W_INTERNATIONAL slot 390: USA - Womens NT
- TXT_W_INTERNATIONAL slot 391: China - Womens NT
- TXT_W_INTERNATIONAL slot 392: Denmark - Womens NT