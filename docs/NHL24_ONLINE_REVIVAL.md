# Bringing NHL 24 (PC) game modes back online — study

Date: 2026-11-16. Two parts: (1) what the uploaded **RinkSide** installer actually is, and (2) what NHL 24 itself
needs to come back online, read out of the game. Ends with an honest verdict and next steps.

This is a feasibility study. Nothing was installed or run; the RinkSide installer and the game were only read.

## 1. RinkSide / zamboni.gg — what it is

The uploaded `RinkSide-Setup.exe` (11.4 MB) is a **web-installer stub** (NSIS wrapper around a **Tauri** app — Rust +
system webview, not Electron). Being only 11 MB, it downloads the real app at install time. Strings pulled from it:

- **`zamboni.gg`** — the project's site; registers a **`rinkside://`** custom URL protocol.
- **`nhllegacy.duckdns.org`** — a dynamic-DNS host; the private **server** the client talks to.
- **"proxy"** appears ~29× — the client works as a local **proxy/redirector** for the game's online traffic.

**What zamboni.gg actually supports (from their site + Discord):** online play for **classic NHL — NHL 10, 11, 12
(with HUT), 13, 14, 15 and NHL Legacy — running on RPCS3** (the PS3 emulator). They "rebuilt the multiplayer servers"
for those titles. **NHL 24 is not supported and is not on their list.** RinkSide is their launcher/proxy for the
*Legacy Edition PC port* online, i.e. the PS3-era NHL games.

**So RinkSide is the model, not a solution.** It proves a small community can stand up private servers and a client
that redirects an old EA hockey game to them. But those are PS3-era titles with the older EA online stack. NHL 24 is a
2023 Frostbite game with EA's **modern** backend, which is a different and much larger problem (section 2–3).

> Caution before running it: it's an unsigned downloader that pulls code from a third-party host and is built to proxy
> network traffic (it may install a local proxy / trust a certificate / edit hosts). It targets the classic NHL games,
> not NHL 24, so installing it does nothing for your NHL 24 build. If you want to explore it, do it in a VM.

## 2. What NHL 24 calls home to (read from the game)

NHL 24 PC here is the PS4 build (CUSA37933) under the shadPS4-fork emulator (`nhl24_new.exe`). Its online stack, from
the eboot and the emulator log:

- **Identity / login — EA Nucleus via the EADP SDK (v1.22.1).** Hosts: `accounts.ea.com`, `accounts.grpc.ea.com`,
  `gateway.ea.com` (+ `.int`/`.lt`/`.grpc` variants). The game logs `EadpFoundation module version=1.22.1`,
  `EadpErrorsData 1.4.0`, and looks for a config file **`/app0/eadpsdk.json`** (and `/app0/assets/eadpsdk.json`) —
  **neither exists**, so the SDK uses its built-in production endpoints.
- **Matchmaking / game modes — EA Blaze.** The eboot is full of `Blaze*` symbols. The client first hits a
  **redirector** to find its Blaze instance:
  - Redirector host: **`spring18.gosredirector.ea.com`** (prod), with `.scert` / `.sdev` / `.stest` test variants.
  - Blaze **service name: `nhl-2024`** (what the redirector resolves to a server, via `/getServerInstance`).
  - Everything is TLS on **:443**.
- **Supporting services:** social/groups (`*.social.ea.com`), leaderboards (`*.leaderboards.ea.com`), MCR
  (`api.mcr.ea.com`), telemetry/errors (`collector.errors.ea.com`, `a-collector.errors.ea.com`), experimentation.
- **Config surface (EADP):** the SDK reads keys like `environment` / `env` (prod, stest, scert, sdev, int, lt, e2e,
  cert, dev), `host`, `baseUrl`, `endpoints`, `overrides`. This is the intended place to repoint the stack — a custom
  `eadpsdk.json` could, in principle, aim identity/telemetry at a private environment without patching the binary.

**Current state:** fully offline. The emulator has `connected_to_network=false` and `shad_net_enabled=false`;
`sceNpGetOnlineId` returns `SIGNED_OUT`; online-only resources (e.g. `onlineplayerclass.brk`) aren't even loaded. So
nothing is reaching EA today — the game isn't trying hard because the emulator's network layer is off and it's signed
out of PSN.

## 3. What "back online" actually requires

To revive a mode (EASHL/World of CHEL, HUT, online versus, online franchise), you must stand in for the whole chain the
game expects. In rough order:

1. **Emulator networking.** The PS4 emulator must do real sockets + PSN sign-in well enough for the game to attempt
   online. shadPS4's net layer is early and off here; this alone is non-trivial and is upstream of everything else.
2. **Redirect EA hostnames to your server.** Point `spring18.gosredirector.ea.com`, `accounts.ea.com`,
   `gateway.ea.com`, etc. at your own host (hosts file / DNS / the emulator's socket layer / a local proxy — RinkSide's
   model). Because it's all TLS, you also need the client to **trust your certificate** for those names (a custom CA or
   cert pinning bypass), or to terminate TLS where you control the trust store.
3. **Reimplement Nucleus / EADP identity.** Issue the tokens the EADP SDK and Blaze login expect. A custom
   `eadpsdk.json` pointing at a private environment is the likely injection point, but the auth handshake still has to
   be reproduced.
4. **Reimplement Blaze for the `nhl-2024` service.** The redirector tells the client where Blaze is; then you must
   speak NHL 24's Blaze protocol: login, user sessions, matchmaking, game creation/joining, and the per-mode RPCs
   (`BlazeClubSettings`, `BlazeGameFromTemplate`, game reporting, etc.). This is the bulk of the work and is
   title-and-version specific — a real reverse-engineering project, captured from live traffic or ported from an
   existing Blaze reimplementation and adapted to NHL 24's components.
5. **Per-mode server logic + persistence** (clubs/rosters, HUT economy, stats) for whichever modes you want to actually
   work, versus just connecting.

**Difficulty verdict (honest):** this is a large undertaking, well beyond the texture/roster/commentary modding so far.
The classic NHL revivals (what zamboni/RinkSide did) target the simpler, older EA online stack on a mature emulator
(RPCS3). NHL 24 combines a modern **EADP + Blaze** backend with an **immature PS4 emulator net layer**. Reviving even
"online versus" is a multi-person, months-scale effort; full mode parity is larger still. It is not impossible — Blaze
reimplementations exist for other EA titles — but it is a project, not a patch.

## 4. Realistic next steps

Cheapest-to-most-expensive, each de-risks the next:

1. **Talk to the zamboni.gg / RinkSide people first.** They've already solved this class of problem and have the server
   code and know-how. Ask directly whether NHL 24 (the PS4 build under shadPS4) is on their radar, and whether their
   server/client could be extended to it. This could save the entire reverse-engineering effort. (Their Discord is the
   contact point.)
2. **Confirm the emulator can even get online.** Before any server work, get shadPS4's net layer + a PSN stub to the
   point where NHL 24 actually tries to reach the redirector. If the emulator can't do online sockets for this title,
   nothing downstream matters yet.
3. **Capture the handshake.** With networking on and EA hosts redirected to a box you control (logging TLS), watch what
   NHL 24 sends to the redirector, Nucleus and Blaze. That traffic is the spec for what a private server must answer.
4. **Try the `eadpsdk.json` override** to point identity/telemetry at a test environment — the least invasive repoint
   point, and a quick read on how much is config-driven vs. baked into the binary.
5. **Only then** decide whether to build a Blaze/Nucleus stand-in for `nhl-2024` or to ride on someone else's.

## Key facts (for a later session)

- RinkSide installer: Tauri web-stub; `zamboni.gg`, `rinkside://`, server `nhllegacy.duckdns.org`, acts as a proxy.
  zamboni supports classic NHL (10–15 + Legacy) on **RPCS3**, **not NHL 24**.
- NHL 24 online: EADP SDK 1.22.1 + Nucleus (`accounts.ea.com`, `gateway.ea.com`), redirector
  **`spring18.gosredirector.ea.com`**, Blaze service **`nhl-2024`**, TLS :443. Config via **`/app0/eadpsdk.json`**
  (absent). Currently signed out, emulator net off.
- The work splits into: emulator networking → hostname redirect + TLS trust → Nucleus/EADP stand-in → Blaze
  reimplementation for `nhl-2024` → per-mode logic. Start by contacting zamboni and capturing the handshake.
