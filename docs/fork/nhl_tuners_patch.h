// SPDX-License-Identifier: GPL-2.0-or-later
// nhlport: gameplay tuner overrides. EA's tuning globals (the g* names — skating speed, hit power, pressure,
// goalie energy…) are compiled into the eboot and registered into a runtime table at boot:
// an array of structs { const char* name; float value; float min; float max; } whose name pointers reference
// the eboot's NUL-separated string table. With NHLPORT_TUNERS=1 a background thread finds that table in guest
// memory (name bytes as the needle; a candidate only qualifies when its name pointer sits in a run of >= 4
// g* pointers at a consistent stride — the real table is an array of hundreds; stray pointers are everywhere
// and must never be written) and applies user\tuner_overrides.json, e.g. {"gSkatingSpeedSlider": 45.0}.
// Re-applies every few seconds because the game can reset values between periods. The Center Ice Modding
// Suite's Gameplay tweakables page writes the same file. No game file is changed; stock returns with the
// env var off.
//
// Drop-in: place next to nhl_teams_patch.h, call NhlTuners::Start() beside NhlTeams::Start(), build.
#pragma once

#ifdef _WIN32

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>

#include "common/logging/log.h"
#include "common/types.h"
#include "core/memory.h"

namespace Core::NhlTuners {

struct Override {
    std::string name;
    float value;
};

// Minimal {"name": number, ...} scan — the suite writes exactly this shape.
inline std::vector<Override> LoadOverrides(const char* path) {
    FILE* f = nullptr;
    if (fopen_s(&f, path, "rb") != 0 || !f) return {};
    std::string s;
    char buf[4096];
    size_t n;
    while ((n = fread(buf, 1, sizeof buf, f)) > 0) s.append(buf, n);
    fclose(f);
    std::vector<Override> out;
    for (size_t i = 0; i + 1 < s.size();) {
        if (s[i] == '"') {
            size_t e = s.find('"', i + 1);
            if (e == std::string::npos) break;
            std::string key = s.substr(i + 1, e - i - 1);
            size_t c = s.find(':', e);
            if (c == std::string::npos) break;
            const char* p = s.c_str() + c + 1;
            char* end = nullptr;
            float v = std::strtof(p, &end);
            if (end != p && !key.empty() && key[0] == 'g') out.push_back({key, v});
            i = c + 1;
        } else i++;
    }
    return out;
}

inline bool Readable(const void* p, size_t n) {
    MEMORY_BASIC_INFORMATION mbi{};
    const u8* a = static_cast<const u8*>(p);
    const u8* end = a + n;
    while (a < end) {
        if (!VirtualQuery(a, &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
            (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
            return false;
        if (!(mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ |
                             PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY)))
            return false;
        a = static_cast<const u8*>(mbi.BaseAddress) + mbi.RegionSize;
    }
    return true;
}

inline bool Writable(const void* p, size_t n) {
    MEMORY_BASIC_INFORMATION mbi{};
    const u8* a = static_cast<const u8*>(p);
    const u8* end = a + n;
    while (a < end) {
        if (!VirtualQuery(a, &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
            (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
            return false;
        if (!(mbi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE))) return false;
        a = static_cast<const u8*>(mbi.BaseAddress) + mbi.RegionSize;
    }
    return true;
}

inline bool IsName(const u8* p) {
    if (!Readable(p, 8) || p[0] != 'g' || p[1] < 'A' || p[1] > 'Z') return false;
    for (int i = 2; i < 62; i++) {
        u8 c = p[i];
        if (c == 0) return i >= 5;
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
            return false;
    }
    return false;
}

// Consecutive g* name pointers at `stride` starting from `site` (direction sign included in stride).
inline int ConsecutiveNames(const u8* base, u64 size, u8* site, long stride) {
    int count = 0;
    for (int k = 1; k <= 64; k++) {
        u8* slot = site + stride * k;
        if (slot < base || slot + 8 > base + size) break;
        u64 ptr = *reinterpret_cast<u64*>(slot);
        if (!IsName(reinterpret_cast<u8*>(ptr))) break;
        count++;
    }
    return count;
}

// Applies one override. Returns true when a validated entry was written this pass.
inline bool ApplyOne(u8* base, u64 size, const Override& o) {
    const size_t nl = o.name.size();
    for (u64 i = 0; i + nl + 1 <= size; i++) {
        if (memcmp(base + i, o.name.c_str(), nl) != 0 || base[i + nl] != 0) continue;
        u8* name_addr = base + i;
        // find pointer slots that reference this occurrence, then demand the array shape
        for (u64 s = 0; s + 8 <= size; s += 8) {
            if (*reinterpret_cast<u64*>(base + s) != reinterpret_cast<u64>(name_addr)) continue;
            static const long kStrides[] = {16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128};
            int best = 0;
            for (long st : kStrides) {
                int run = 1 + ConsecutiveNames(base, size, base + s, st) +
                          ConsecutiveNames(base, size, base + s, -st);
                if (run > best) best = run;
            }
            if (best < 4) continue;                      // not the tuner table — never write here
            float* value = reinterpret_cast<float*>(base + s + 8);
            if (!Writable(value, 4)) continue;
            *value = o.value;                            // {namePtr, value, min, max}
            return true;
        }
    }
    return false;
}

inline void ScanRange(u8* base, u64 size, const std::vector<Override>& ovs, int* applied) {
    for (const auto& o : ovs) {
        if (ApplyOne(base, size, o)) (*applied)++;
    }
}

inline void Start() {
    std::thread([] {
        if (const char* env = std::getenv("NHLPORT_TUNERS"); !env || env[0] != '1') return;
        const char* root = std::getenv("NHLPORT_ROOT");   // emulator exe dir; overrides live in <root>\user
        char path[MAX_PATH] = "user\\tuner_overrides.json";
        if (root && root[0]) snprintf(path, sizeof path, "%s\\user\\tuner_overrides.json", root);
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(300);
        int quiet = 0;
        while (std::chrono::steady_clock::now() < deadline || quiet < 3) {
            auto ovs = LoadOverrides(path);
            if (ovs.empty()) { std::this_thread::sleep_for(std::chrono::seconds(5)); continue; }
            int applied = 0;
            // the same three guest ranges nhl_teams_patch scans
            for (auto* memory : {&Core::Memory::Instance()->GetAddressSpace().SystemManagedVirtualBase(),
                                 &Core::Memory::Instance()->GetAddressSpace().SystemReservedVirtualBase(),
                                 &Core::Memory::Instance()->GetAddressSpace().UserVirtualBase()}) {
                u8* base = reinterpret_cast<u8*>(*memory);
                if (!base) continue;
                MEMORY_BASIC_INFORMATION mbi{};
                for (u8* at = base; VirtualQuery(at, &mbi, sizeof mbi) && mbi.State == MEM_COMMIT;) {
                    if ((mbi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE)) &&
                        mbi.RegionSize.QuadPart > 0 && mbi.RegionSize.QuadPart < (1LL << 31))
                        ScanRange(reinterpret_cast<u8*>(mbi.BaseAddress), (u64)mbi.RegionSize.QuadPart, ovs, &applied);
                    u8* next = static_cast<u8*>(mbi.BaseAddress) + mbi.RegionSize.QuadPart;
                    if (next <= at) break;
                    at = next;
                }
            }
            quiet = applied > 0 ? 0 : quiet + 1;
            if (applied > 0) printf("nhlport: %d tuner override(s) applied\n", applied);
            std::this_thread::sleep_for(std::chrono::seconds(5));
        }
    }).detach();
}

} // namespace Core::NhlTuners

#endif // _WIN32
