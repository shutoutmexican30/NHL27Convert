"""
Stock Team Equipment Editor for NHL 14
Directly reads and writes EA .DB files (no CSV needed).
Tables: hHbv (Stock Team Equipment), xjgw (Team Jerseys)
"""

import struct, os, shutil, sys, threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, colorchooser

# ── Optional 3-D preview via self-contained helper ────────────────────────────
_ROSTER_DIR = os.path.dirname(os.path.abspath(__file__))

_PREVIEW_OK    = False
_GL_OK_PREVIEW = False
_PREVIEW_ERR   = ""

try:
    import eq_3d_helpers as _te
    _PREVIEW_OK    = True
    _GL_OK_PREVIEW = _te._GL_OK
except Exception as _e:
    import traceback as _tb
    _PREVIEW_ERR = _tb.format_exc()
    _te = None

_EQUIP_CONFIG = {
    5: {
        "rx2":        os.path.join(_ROSTER_DIR, "helmet-gloves/feprop_glove_0.rx2"),
        "zone_masks": [
            os.path.join(_ROSTER_DIR, "helmet-gloves/feglove_15_0_tm.rpsgl"),
            os.path.join(_ROSTER_DIR, "helmet-gloves/feglove_15_1_tm.rpsgl"),
            os.path.join(_ROSTER_DIR, "helmet-gloves/feglove_15_2_tm.rpsgl"),
        ],
        "detail": None,
    },
    8: {
        "rx2":              os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_14_2.rx2"),
        "zone_masks":       [os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_0_15_tm.rpsgl")],
        "detail":           os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_0_15_0_dm.rpsgl"),
        "material_rpsgl":   os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_14_2.rpsgl"),
        "nm_rpsgl":         os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_0_15_nm.rpsgl"),
        "sm_rpsgl":         os.path.join(_ROSTER_DIR, "helmet-gloves/helmet_0_15_sm.rpsgl"),
        # TM is sampled at UV1 − UV2 per vertex (UV2 varies per vertex in the RX2).
        "tm_uses_uv2_offset": True,
        "uv_v_offset": -0.07,
    },
}

# ── Field mappings — Stock Team Equipment (hHbv) ──────────────────────────────

TARGET_TABLE = "hHbv"
LANG_TABLE   = "vCJG"

FIELD_MAP = {
    "vfEq": "Index",      "dXSB": "Team ID",    "jJpM": "Equipment",
    "TKWL": "Brand",      "VhbR": "Era",         "FccT": "Variant",
    "IByh": "Zone 1 C1",  "QnCG": "Zone 1 C2",  "VaOV": "Zone 1 C3",
    "GHIQ": "Zone 2 C1",  "OZgb": "Zone 2 C2",  "Tssg": "Zone 2 C3",
    "HZib": "Zone 3 C1",  "PLSw": "Zone 3 C2",  "SCSn": "Zone 3 C3",
    "KuQC": "Zone 4 C1",  "SwoV": "Zone 4 C2",  "PVgG": "Zone 4 C3",
    "LIml": "Zone 5 C1",  "TeyG": "Zone 5 C2",  "OjCJ": "Zone 5 C3",
    "NOUA": "Zone 6 C1",  "VQkn": "Zone 6 C2",  "QBEw": "Zone 6 C3",
    "MgSz": "Zone 7 C1",  "UCSg": "Zone 7 C2",  "RLCb": "Zone 7 C3",
    "DYdG": "Zone 8 C1",  "LmNh": "Zone 8 C2",  "WbNC": "Zone 8 C3",
    "CiDJ": "Zone 9 C1",  "KYzW": "Zone 9 C2",  "Xtnl": "Zone 9 C3",
}

INFO_FIELDS  = ["vfEq", "dXSB", "jJpM", "TKWL", "VhbR", "FccT"]
ZONE_ABBREVS = [
    ["IByh","QnCG","VaOV"], ["GHIQ","OZgb","Tssg"], ["HZib","PLSw","SCSn"],
    ["KuQC","SwoV","PVgG"], ["LIml","TeyG","OjCJ"], ["NOUA","VQkn","QBEw"],
    ["MgSz","UCSg","RLCb"], ["DYdG","LmNh","WbNC"], ["CiDJ","KYzW","Xtnl"],
]

# Color channels: C1=Blue, C2=Green, C3=Red  (BGR order in DB)
EQUIP_NAMES = {5: "Gloves", 8: "Helmet"}

ZONE_NAMES = {
    8: ["Main Color", "Strap Color", "Logo",
        "Zone 4", "Zone 5", "Zone 6", "Zone 7", "Zone 8", "Zone 9"],
    5: ["Main Color", "Middle Color", "Middle Piping", "Thumb Piping",
        "Lower Cuff", "Wrist Main Color", "Wrist Logo", "Thumb Color",
        "Extra Piping (Thumb)"],
}

# ── Field mappings — Team Jerseys (xjgw) ──────────────────────────────────────

JERSEY_TABLE = "xjgw"

# ordered for display
JERSEY_FIELDS = ["mCXg","tYnx","cVLR","GDQL","JpWa","aEfN","Ughr","tWcJ",
                 "UuZg","yFSM","vInP","FccT","ddOX"]

JERSEY_FIELD_MAP = {
    "mCXg": "Team ID",
    "tYnx": "Jersey Index",
    "cVLR": "Jersey Style",
    "GDQL": "Team Art ID",
    "JpWa": "Helmet Logo",
    "aEfN": "Helmet Numbers",
    "Ughr": "Is Light Color",
    "tWcJ": "Has Tie",
    "UuZg": "Loc String",
    "yFSM": "Is Default",
    "vInP": "Is Lockable",
    "FccT": "Variant",
    "ddOX": "FE Variant",
}

JERSEY_FIELD_HINTS = {
    "cVLR": "0 = 90s/CCM/Retro   1 = Reebok/Modern",
    "Ughr": "0 = Home (dark)   1 = Away (light)",
    "aEfN": "0 = no numbers   1 = has numbers",
    "tWcJ": "0 = no laces   1 = has laces",
    "yFSM": "0 = not default   1 = default",
    "vInP": "0 = unlocked   1 = locked",
    "UuZg": "0=Home  1=Away  2=Alt  3=2006-07 Home  4=2006-07 Away  5=2006-07 2nd Home  6=Vintage (auto key)",
}

JERSEY_STYLE_NAMES = {0: "90s/CCM/Retro", 1: "Reebok/Modern"}
LIGHT_COLOR_NAMES  = {0: "Home", 1: "Away"}
LOC_STRING_NAMES   = {
    0: "Home",
    1: "Away",
    2: "Alt",
    3: "2006-07 Home",
    4: "2006-07 Away",
    5: "2006-07 2nd Home",
    6: "Vintage (auto key)",
}

# ── Field mappings — Team Settings (kOtt) ─────────────────────────────────────

TEAM_TABLE = "kOtt"

TEAM_FIELD_MAP = {
    "vfEq": "Index",
    "YmkJ": "Team Name",
    "QNTI": "Team City",
    "xsnn": "Abbreviation",
    "rbPR": "Art Abbreviation",
    "xMjj": "League",
    "iIBl": "League Group",
    "OwTu": "Conference",
    "CGMK": "Division",
    "erSL": "Rival Team 1",
    "SqVn": "Rival Team 2",
    "lOnr": "Portrait ID",
    "oazN": "Audio ID",
    "MSly": "Audio ID 2",
    "CsxT": "AHL Affiliate",
    "ATUU": "Coach ID",
    "buDa": "Arena ID",
    "TTmQ": "Tunnel",
    "StZX": "Spotlight",
    "ZXmA": "1st Color Red",
    "Mqej": "1st Color Green",
    "EUWQ": "1st Color Blue",
    "XLfd": "2nd Color Red",
    "iCHm": "2nd Color Green",
    "qEvF": "2nd Color Blue",
}

# Display groups
TEAM_IDENTITY_FIELDS = ["vfEq", "YmkJ", "QNTI", "xsnn", "rbPR"]
TEAM_LEAGUE_FIELDS   = ["xMjj", "iIBl", "OwTu", "CGMK"]
TEAM_RIVAL_FIELDS    = ["erSL", "SqVn"]
TEAM_ID_FIELDS       = ["lOnr", "oazN", "MSly", "CsxT", "ATUU", "buDa", "TTmQ", "StZX"]
# Colors: stored as Red, Green, Blue channels
TEAM_COLOR1 = ("ZXmA", "Mqej", "EUWQ")   # R, G, B
TEAM_COLOR2 = ("XLfd", "iCHm", "qEvF")   # R, G, B

# ── Team / era / equip helpers ─────────────────────────────────────────────────

TEAM_NAMES = {
    1:  "Anaheim Ducks",
    2:  "Winnipeg Jets",
    3:  "Boston Bruins",
    4:  "Buffalo Sabres",
    5:  "Calgary Flames",
    6:  "Carolina Hurricanes",
    7:  "Chicago Blackhawks",
    8:  "Colorado Avalanche",
    9:  "Columbus Blue Jackets",
    10: "Dallas Stars",
    11: "Detroit Red Wings",
    12: "Edmonton Oilers",
    13: "Florida Panthers",
    14: "Los Angeles Kings",
    15: "Minnesota Wild",
    16: "Montreal Canadiens",
    17: "Nashville Predators",
    18: "New Jersey Devils",
    19: "New York Islanders",
    20: "New York Rangers",
    21: "Ottawa Senators",
    22: "Philadelphia Flyers",
    23: "Arizona Coyotes",
    24: "Pittsburgh Penguins",
    25: "St. Louis Blues",
    26: "San Jose Sharks",
    27: "Tampa Bay Lightning",
    28: "Toronto Maple Leafs",
    29: "Vancouver Canucks",
    30: "Washington Capitals",
    31: "White/Silver",
    32: "Yellow/Black",
    # AHL
    33: "Abbotsford Heat",
    34: "Albany Devils",
    35: "Binghamton Senators",
    36: "Charlotte Checkers",
    37: "Chicago Wolves",
    38: "Cleveland Monsters",
    39: "Connecticut Whale",
    40: "Hamilton Bulldogs",
    41: "Hershey Bears",
    42: "Houston Aeros",
    43: "Iowa Wild",
    44: "Lake Erie Monsters",
    45: "Lehigh Valley Phantoms",
    46: "Manchester Monarchs",
    47: "Milwaukee Admirals",
    48: "Norfolk Admirals",
    49: "Oklahoma City Barons",
    50: "Peoria Rivermen",
    51: "Portland Pirates",
    52: "Providence Bruins",
    53: "Rochester Americans",
    54: "Rockford IceHogs",
    55: "San Antonio Rampage",
    56: "Springfield Falcons",
    57: "St. John's IceCaps",
    58: "Syracuse Crunch",
    59: "Toronto Marlies",
    60: "Utica Comets",
    61: "Worcester Sharks",
    # SHL (Swedish)
    62: "Djurgarden",
    63: "Farjestad",
    64: "Frolunda",
    65: "HV71",
    66: "MODO",
    67: "MIF",
    68: "Leksand",
    69: "Linkoping",
    70: "Lulea",
    71: "Orebro",
    72: "Oskarshamn",
    73: "Rogle",
    74: "Skelleftea",
    75: "Timra",
    76: "Vaxjo",
    # Liiga (Finnish)
    77: "Ässät",
    78: "HPK",
    79: "IFK Helsinki",
    80: "Ilves",
    81: "JYP",
    82: "Jokerit",
    83: "KalPa",
    84: "Karpat",
    85: "KooKoo",
    86: "Lahti Pelicans",
    87: "Lukko",
    88: "Pelicans",
    89: "SaiPa",
    90: "Sport",
    91: "TPS",
    92: "Tappara",
    # DEL (German)
    93: "Adler Mannheim",
    94: "Augsburger Panther",
    95: "DEG Metro Stars",
    96: "EHC Munchen",
    97: "ERC Ingolstadt",
    98: "Eisbaren Berlin",
    99: "Grizzlys Wolfsburg",
    100: "Hamburg Freezers",
    101: "Hannover Scorpions",
    102: "Iserlohn Roosters",
    103: "Kolner Haie",
    104: "Krefeld Pinguine",
    105: "Nurnberg Ice Tigers",
    106: "Schwenninger Wild Wings",
    107: "Straubing Tigers",
    108: "Thomas Sabo Ice Tigers",
    # Czech
    109: "Bili Tygri Liberec",
    110: "Bily Vlci Plzen",
    111: "HC Kometa Brno",
    112: "HC Ocelari Trinec",
    113: "HC Orava Dolny Kubin",
    114: "HC Pardubice",
    115: "HC Slavia Praha",
    116: "HC Sparta Praha",
    117: "HC Vitkovice Steel",
    118: "HC Zlin",
    119: "Mlada Boleslav",
    120: "Motor Ceske Budejovice",
    121: "Pirani Benatky nad Jizerou",
    122: "Rytiri Kladno",
    # Swiss
    123: "Ambri-Piotta",
    124: "Bern",
    125: "Biel",
    126: "Davos",
    127: "Fribourg-Gotteron",
    128: "Geneve-Servette",
    129: "Kloten",
    130: "Lakers",
    131: "Lausanne",
    132: "Lugano",
    133: "Rapperswil-Jona",
    134: "Zug",
    135: "Zurich Lions",
    # National teams
    136: "Canada",
    137: "Czech Republic",
    138: "Denmark",
    139: "Finland",
    140: "France",
    141: "Germany",
    142: "Great Britain",
    143: "Latvia",
    144: "Norway",
    145: "Russia",
    146: "Slovakia",
    147: "Sweden",
    148: "Switzerland",
    149: "Ukraine",
    150: "USA",
    # CHL - OHL
    151: "Barrie Colts",
    152: "Belleville Bulls",
    153: "Brampton Battalion",
    154: "Erie Otters",
    155: "Guelph Storm",
    156: "Kingston Frontenacs",
    157: "Kitchener Rangers",
    158: "London Knights",
    159: "Mississauga Steelheads",
    160: "Niagara IceDogs",
    161: "North Bay Battalion",
    162: "Oshawa Generals",
    163: "Ottawa 67s",
    164: "Owen Sound Attack",
    165: "Peterborough Petes",
    166: "Plymouth Whalers",
    167: "Saginaw Spirit",
    168: "Sarnia Sting",
    169: "Sault Ste. Marie Greyhounds",
    170: "Sudbury Wolves",
    171: "Windsor Spitfires",
    # CHL - QMJHL
    172: "Baie-Comeau Drakkar",
    173: "Blainville-Boisbriand Armada",
    174: "Cape Breton Screaming Eagles",
    175: "Chicoutimi Sagueneens",
    176: "Drummondville Voltigeurs",
    177: "Gatineau Olympiques",
    178: "Halifax Mooseheads",
    179: "Moncton Wildcats",
    180: "PEI Rocket",
    181: "Quebec Remparts",
    182: "Rimouski Oceanic",
    183: "Rouyn-Noranda Huskies",
    184: "Saint John Sea Dogs",
    185: "Shawinigan Cataractes",
    186: "Sherbrooke Phoenix",
    187: "Tri-City Americans",
    188: "Val-d'Or Foreurs",
    189: "Victoriaville Tigres",
    # CHL - WHL
    190: "Brandon Wheat Kings",
    191: "Calgary Hitmen",
    192: "Edmonton Oil Kings",
    193: "Everett Silvertips",
    194: "Kamloops Blazers",
    195: "Kelowna Rockets",
    196: "Kootenay Ice",
    197: "Lethbridge Hurricanes",
    198: "Medicine Hat Tigers",
    199: "Moose Jaw Warriors",
    200: "Portland Winterhawks",
    201: "Prince Albert Raiders",
    202: "Prince George Cougars",
    203: "Red Deer Rebels",
    204: "Regina Pats",
    205: "Saskatoon Blades",
    206: "Seattle Thunderbirds",
    207: "Spokane Chiefs",
    208: "Swift Current Broncos",
    209: "Tri-City Americans",
    210: "Vancouver Giants",
    211: "Victoria Royals",
    212: "Winnipeg Ice",
}

ERA_NAMES = {0: "Reebok", 1: "90's"}

def get_team_name(raw_val):
    """1-indexed team ID (used by Stock Team Equipment)."""
    try:
        v = int(raw_val)
        return TEAM_NAMES.get(v, f"Team {v}")
    except (ValueError, TypeError):
        return str(raw_val)

def get_jersey_team_name(raw_val):
    """0-indexed team ID (used by Team Jerseys)."""
    try:
        v = int(raw_val)
        return TEAM_NAMES.get(v + 1, f"Team {v}")
    except (ValueError, TypeError):
        return str(raw_val)

def get_era_name(raw_val):
    try:
        v = int(raw_val)
        return ERA_NAMES.get(v, f"Era {v}")
    except (ValueError, TypeError):
        return str(raw_val)

def get_equip_name(raw_val):
    try:
        v = int(raw_val)
        return EQUIP_NAMES.get(v, f"Equipment {v}")
    except (ValueError, TypeError):
        return str(raw_val)

def get_zone_name(equip_raw, zone_idx):
    try:
        v = int(equip_raw)
    except (ValueError, TypeError):
        return f"Zone {zone_idx+1}"
    names = ZONE_NAMES.get(v)
    if names and zone_idx < len(names):
        return names[zone_idx]
    return f"Zone {zone_idx+1}"

# ── CRC-32 big-endian (EA variant) ────────────────────────────────────────────

def _build_crc_table():
    POLY, table = 0x04C11DB7, [0]*16
    crc = 0x80000000
    i = 1
    while i < 16:
        crc = (((crc<<1) ^ POLY) if (crc & 0x80000000) else (crc<<1)) & 0xFFFFFFFF
        for j in range(i):
            table[i+j] = (crc ^ table[j]) & 0xFFFFFFFF
        i <<= 1
    return table

_CRC_TABLE = _build_crc_table()

def _crc32_be_raw(data, length, start=0):
    crc = 0xFFFFFFFF
    for i in range(length):
        crc ^= data[start+i] << 24
        crc &= 0xFFFFFFFF
        crc = ((crc<<4)&0xFFFFFFFF) ^ _CRC_TABLE[(crc>>28)&0xF]
        crc = ((crc<<4)&0xFFFFFFFF) ^ _CRC_TABLE[(crc>>28)&0xF]
    return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF

def calc_crc(data, length, start=0):
    return (~_crc32_be_raw(data, length, start)) & 0xFFFFFFFF

# ── Bit-stream utilities (big-endian bit packing) ─────────────────────────────

def read_bits(data, bit_offset, bit_count):
    result = 0
    for i in range(bit_count):
        byte_idx = (bit_offset + i) >> 3
        bit_idx  = 7 - ((bit_offset + i) & 7)
        result   = (result << 1) | ((data[byte_idx] >> bit_idx) & 1)
    return result

def write_bits(data, bit_offset, bit_count, value):
    mask = 1 << (bit_count - 1)
    for i in range(bit_count):
        byte_idx = (bit_offset + i) >> 3
        bit_idx  = 7 - ((bit_offset + i) & 7)
        if value & mask:
            data[byte_idx] |= (1 << bit_idx)
        else:
            data[byte_idx] &= ~(1 << bit_idx) & 0xFF
        mask >>= 1

# ── DB structures ─────────────────────────────────────────────────────────────

FTYPE_STRING, FTYPE_BINARY, FTYPE_SINT, FTYPE_UINT, FTYPE_FLOAT = 0,1,2,3,4
INFO_SIZE  = 40
FIELD_SIZE = 16

class EAField:
    def __init__(self, name, ftype, bit_offset, bit_count):
        self.name       = name
        self.ftype      = ftype
        self.bit_offset = bit_offset
        self.bit_count  = bit_count

    def read(self, raw):
        if self.ftype == FTYPE_STRING:
            bo = self.bit_offset >> 3
            bc = self.bit_count  >> 3
            return raw[bo:bo+bc].rstrip(b'\x00').decode('ascii', errors='replace')
        if self.ftype == FTYPE_BINARY:
            bo, bc = self.bit_offset>>3, self.bit_count>>3
            return ''.join(f'{raw[bo+i]:02X}' for i in range(bc))
        v = read_bits(raw, self.bit_offset, self.bit_count)
        if self.ftype == FTYPE_SINT:
            v = (v ^ (1<<(self.bit_count-1))) - (1<<(self.bit_count-1))
        return str(v)

    def write(self, raw, value):
        if self.ftype == FTYPE_STRING:
            bo, bc = self.bit_offset>>3, self.bit_count>>3
            for i in range(bc):
                raw[bo+i] = ord(value[i]) if i < len(value) else 0
            return
        if self.ftype == FTYPE_BINARY:
            bo, bc = self.bit_offset>>3, self.bit_count>>3
            v = value.ljust(bc*2,'0')
            for i in range(bc):
                raw[bo+i] = int(v[i*2:i*2+2], 16)
            return
        try:
            iv = int(value)
        except ValueError:
            return
        iv = iv & ((1 << self.bit_count) - 1)
        write_bits(raw, self.bit_offset, self.bit_count, iv)


class EARecord:
    def __init__(self, fields, raw_bytes):
        self.fields    = fields
        self._fmap     = {f.name: f for f in fields}
        self.raw       = bytearray(raw_bytes)

    def get(self, name):
        f = self._fmap.get(name)
        return f.read(self.raw) if f else ""

    def set(self, name, value):
        f = self._fmap.get(name)
        if f:
            f.write(self.raw, value)


class EATable:
    def __init__(self):
        self.name = ""; self.offset_from_index = 0
        self.prior_crc = 0; self.unknown_2 = 0
        self.len_bytes = 0; self.len_bits = 0; self.zero = 0
        self.max_records = 0; self.cur_records = 0
        self.unknown_3 = 0; self.num_fields = 0; self.index_count = 0
        self.zero2 = 0; self.zero3 = 0; self.header_crc = 0
        self.field_start = 0; self.data_start = 0
        self.fields  = []
        self.records = []


class EADatabase:
    def __init__(self):
        self.data         = bytearray()
        self.header_word  = 0; self.version = 0; self.unknown_1 = 0
        self.db_size      = 0; self.zero = 0
        self.table_count  = 0; self.header_crc = 0
        self.start_data   = 0
        self.tables       = []
        self.path         = ""

    def load(self, path):
        with open(path, "rb") as f:
            self.data = bytearray(f.read())
        self.path = path
        d = self.data

        if d[0] != ord('D') or d[1] != ord('B'):
            raise ValueError("Not a valid EA .DB file (missing 'DB' magic)")

        self.header_word = struct.unpack_from(">H", d, 0)[0]
        self.version     = struct.unpack_from(">H", d, 2)[0]
        self.unknown_1   = struct.unpack_from(">I", d, 4)[0]
        self.db_size     = struct.unpack_from(">I", d, 8)[0]
        self.zero        = struct.unpack_from(">I", d,12)[0]
        self.table_count = struct.unpack_from(">I", d,16)[0]
        self.header_crc  = struct.unpack_from(">I", d,20)[0]

        pos = 24
        self.tables = []
        for _ in range(self.table_count):
            t = EATable()
            t.name = chr(d[pos+3])+chr(d[pos+2])+chr(d[pos+1])+chr(d[pos])
            t.offset_from_index = struct.unpack_from(">I", d, pos+4)[0]
            self.tables.append(t)
            pos += 8

        self.start_data = pos

        for t in self.tables:
            hp = self.start_data + t.offset_from_index
            t.prior_crc   = struct.unpack_from(">I", d, hp+ 0)[0]
            t.unknown_2   = struct.unpack_from(">I", d, hp+ 4)[0]
            t.len_bytes   = struct.unpack_from(">I", d, hp+ 8)[0]
            t.len_bits    = struct.unpack_from(">I", d, hp+12)[0]
            t.zero        = struct.unpack_from(">I", d, hp+16)[0]
            t.max_records = struct.unpack_from(">H", d, hp+20)[0]
            t.cur_records = struct.unpack_from(">H", d, hp+22)[0]
            t.unknown_3   = struct.unpack_from(">I", d, hp+24)[0]
            t.num_fields  = d[hp+28]
            t.index_count = d[hp+29]
            t.zero2       = struct.unpack_from(">H", d, hp+30)[0]
            t.zero3       = struct.unpack_from(">I", d, hp+32)[0]
            t.header_crc  = struct.unpack_from(">I", d, hp+36)[0]
            t.field_start = hp + INFO_SIZE
            t.data_start  = t.field_start + t.num_fields * FIELD_SIZE

            t.fields = []
            for i in range(t.num_fields):
                fp     = t.field_start + i * FIELD_SIZE
                ftype  = struct.unpack_from(">I", d, fp+ 0)[0]
                foff   = struct.unpack_from(">I", d, fp+ 4)[0]
                fname  = chr(d[fp+11])+chr(d[fp+10])+chr(d[fp+9])+chr(d[fp+8])
                fbits  = struct.unpack_from(">I", d, fp+12)[0]
                t.fields.append(EAField(fname, ftype, foff, fbits))

            t.records = []
            for i in range(t.cur_records):
                rs = t.data_start + i * t.len_bytes
                t.records.append(EARecord(t.fields, d[rs:rs+t.len_bytes]))

    def get_table(self, name):
        for t in self.tables:
            if t.name == name:
                return t
        return None

    def save(self, path=None):
        if path is None:
            path = self.path
        d = self.data

        for t in self.tables:
            hp = self.start_data + t.offset_from_index
            struct.pack_into(">I", d, hp+ 0, t.prior_crc)
            struct.pack_into(">I", d, hp+ 4, t.unknown_2)
            struct.pack_into(">I", d, hp+ 8, t.len_bytes)
            struct.pack_into(">I", d, hp+12, t.len_bits)
            struct.pack_into(">I", d, hp+16, t.zero)
            struct.pack_into(">H", d, hp+20, t.max_records)
            struct.pack_into(">H", d, hp+22, t.cur_records)
            struct.pack_into(">I", d, hp+24, t.unknown_3)
            d[hp+28] = t.num_fields
            d[hp+29] = t.index_count
            struct.pack_into(">H", d, hp+30, t.zero2)
            struct.pack_into(">I", d, hp+32, t.zero3)

            for i, f in enumerate(t.fields):
                fp = t.field_start + i * FIELD_SIZE
                struct.pack_into(">I", d, fp+ 0, f.ftype)
                struct.pack_into(">I", d, fp+ 4, f.bit_offset)
                enc = f.name.encode('ascii')
                d[fp+8]=enc[3]; d[fp+9]=enc[2]; d[fp+10]=enc[1]; d[fp+11]=enc[0]
                struct.pack_into(">I", d, fp+12, f.bit_count)

            for i, rec in enumerate(t.records):
                rs = t.data_start + i * t.len_bytes
                d[rs:rs+t.len_bytes] = rec.raw

        self._calc_checksums()

        with open(path, "wb") as f:
            f.write(d[:self.db_size])

        self.path = path

    def _calc_checksums(self):
        d = self.data

        struct.pack_into(">I", d, 20, calc_crc(d, 20, 0))

        prior = calc_crc(d, self.table_count * 8, 24)

        for i, t in enumerate(self.tables):
            hp = self.start_data + t.offset_from_index

            h_crc = calc_crc(d, INFO_SIZE - 8, hp + 4)
            struct.pack_into(">I", d, hp + 36, h_crc)

            struct.pack_into(">I", d, hp, prior)

            data_start = hp + INFO_SIZE
            data_end   = (self.start_data + self.tables[i+1].offset_from_index
                          if i < len(self.tables)-1 else self.db_size - 4)
            prior = calc_crc(d, data_end - data_start, data_start)

        struct.pack_into(">I", d, self.db_size - 4, prior)


# ── DB-type detection ──────────────────────────────────────────────────────────

def detect_db_type(db):
    """Return 'equipment', 'lang', or 'unknown'."""
    names = {t.name for t in db.tables}
    if TARGET_TABLE in names or JERSEY_TABLE in names or TEAM_TABLE in names:
        return "equipment"
    if LANG_TABLE in names:
        return "lang"
    return "unknown"


def load_meta_xml(path):
    """Parse a T3DB meta XML file.
    Returns (table_map, field_map) where:
      table_map: {shortname: longname}
      field_map: {table_shortname: {field_shortname: longname}}
    """
    import xml.etree.ElementTree as ET
    table_map, field_map = {}, {}
    try:
        root = ET.parse(path).getroot()
        for tbl in root.iter("table"):
            tsn = tbl.get("shortname", "")
            tln = tbl.get("name", tsn)
            table_map[tsn] = tln
            field_map[tsn] = {}
            for fld in tbl.iter("field"):
                fsn = fld.get("shortname", "")
                fln = fld.get("name", fsn)
                field_map[tsn][fsn] = fln
    except Exception:
        pass
    return table_map, field_map


# ── GUI helpers ────────────────────────────────────────────────────────────────

def _channel_byte(val_str):
    try: return max(0, min(255, int(val_str)))
    except: return 0


# ── Equipment Preview Window ──────────────────────────────────────────────────

class EquipPreviewWindow(tk.Toplevel):
    """3-D equipment model viewer with a zone-colour legend panel."""

    BG     = "#050505"
    PANEL  = "#0c0c0c"
    SURF   = "#111111"
    BORDER = "#2a2a2a"
    DIM    = "#444444"
    TEXT   = "#dce8f0"
    ACCENT = "#38bdf8"

    def __init__(self, master, zone_colors, zone_names, equip_name="Gloves", eq_type=5):
        super().__init__(master)
        self.title(f"Equipment Preview — {equip_name}")
        self.geometry("1000x640")
        self.configure(bg=self.BG)
        self._zone_colors   = list(zone_colors)
        self._zone_names    = list(zone_names)
        self._equip_name    = equip_name
        self._eq_type       = eq_type
        self._swatch_labels = []
        self._val_labels    = []
        self._gl_view       = None
        self._zone_masks        = []   # list of (arr H×W×3, w, h) per zone-mask rpsgl
        self._detail_arr        = None # (arr H×W×4 RGBA, w, h) for dm overlay
        self._material_overlay  = None # (arr H×W×4 RGBA, w, h) brand-specific overlay
        self._mesh_uv1          = None # (N, 2) float32 — UV1 per vertex
        self._mesh_uv2          = None # (N, 2) float32 — UV2 per vertex, if present
        self._build_ui()
        if _PREVIEW_OK and _GL_OK_PREVIEW:
            self.after(150, self._load_model_async)
        else:
            if not _PREVIEW_OK:
                reason = f"eq_3d_helpers import failed: {_PREVIEW_ERR.splitlines()[-1] if _PREVIEW_ERR else 'unknown error'}"
            else:
                reason = "PyOpenGL / pyopengltk not installed"
            self._set_status(f"3-D viewer unavailable — {reason}")

    def _build_ui(self):
        hdr = tk.Frame(self, bg=self.PANEL, height=42)
        hdr.pack(side=tk.TOP, fill=tk.X)
        hdr.pack_propagate(False)
        tk.Label(hdr, text=f"Equipment Preview — {self._equip_name}",
                 bg=self.PANEL, fg=self.ACCENT,
                 font=("Segoe UI", 11, "bold"), padx=14).pack(side=tk.LEFT, pady=10)
        self._status_var = tk.StringVar(value="Loading…")
        tk.Label(hdr, textvariable=self._status_var, bg=self.PANEL, fg=self.DIM,
                 font=("Segoe UI", 9)).pack(side=tk.RIGHT, padx=14)
        tk.Frame(self, bg=self.BORDER, height=1).pack(fill=tk.X)

        body = tk.Frame(self, bg=self.BG)
        body.pack(fill=tk.BOTH, expand=True)

        # Left: 3-D viewport
        left = tk.Frame(body, bg="#000000")
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        if _PREVIEW_OK and _GL_OK_PREVIEW:
            self._gl_view = _te.ModelGLView(left, width=680, height=580)
            self._gl_view.pack(fill=tk.BOTH, expand=True)
        else:
            tk.Label(
                left,
                text="3-D preview requires PyOpenGL + pyopengltk.\n\n"
                     "pip install PyOpenGL pyopengltk",
                bg="#111111", fg=self.DIM,
                font=("Segoe UI", 10), justify="center",
            ).pack(expand=True)

        # Right: zone colour legend
        right = tk.Frame(body, bg=self.PANEL, width=300)
        right.pack(side=tk.RIGHT, fill=tk.Y)
        right.pack_propagate(False)

        tk.Label(right, text="Zone Colors", bg=self.PANEL, fg=self.ACCENT,
                 font=("Segoe UI", 10, "bold"), padx=14).pack(anchor="w", pady=(12, 4))
        tk.Frame(right, bg=self.BORDER, height=1).pack(fill=tk.X, padx=8, pady=(0, 6))

        for z in range(9):
            row_bg = self.SURF if z % 2 == 0 else self.PANEL
            row    = tk.Frame(right, bg=row_bg)
            row.pack(fill=tk.X, padx=8, pady=1)

            r, g, b = self._zone_colors[z] if z < len(self._zone_colors) else (0, 0, 0)
            hex_col = f"#{r:02X}{g:02X}{b:02X}"

            sw = tk.Label(row, text="", bg=hex_col, width=4, relief="flat")
            sw.pack(side=tk.LEFT, padx=(0, 8), ipadx=4, ipady=8)
            self._swatch_labels.append(sw)

            info = tk.Frame(row, bg=row_bg)
            info.pack(side=tk.LEFT, fill=tk.X, expand=True, pady=4)

            name = self._zone_names[z] if z < len(self._zone_names) else f"Zone {z+1}"
            tk.Label(info, text=f"Zone {z+1}  {name}",
                     bg=row_bg, fg=self.TEXT,
                     font=("Segoe UI", 9, "bold"), anchor="w").pack(anchor="w")

            val_lbl = tk.Label(info,
                               text=f"R:{r}  G:{g}  B:{b}  {hex_col.upper()}",
                               bg=row_bg, fg=self.DIM,
                               font=("Consolas", 8), anchor="w")
            val_lbl.pack(anchor="w")
            self._val_labels.append(val_lbl)

    def _set_status(self, msg: str):
        if hasattr(self, "_status_var"):
            self._status_var.set(msg)

    def _build_colored_texture(self):
        """Combine zone-mask arrays with DB zone colors, then composite any detail overlay."""
        import numpy as np
        THRESH, LOW = 128, 64

        # UV2-corrected path: per-pixel TM lookup via nearest-vertex UV2 interpolation
        if (self._mesh_uv1 is not None and self._mesh_uv2 is not None
                and len(self._zone_masks) >= 1 and self._zone_masks[0] is not None):
            arr, mw, mh = self._zone_masks[0]
            uv1 = self._mesh_uv1
            uv2 = self._mesh_uv2

            # Build KDTree over vertex UV1 positions (in pixel space)
            try:
                from scipy.spatial import KDTree
                uv1_px = np.stack([uv1[:, 0] * mw, uv1[:, 1] * mh], axis=1)
                tree = KDTree(uv1_px)

                # Create pixel grid covering the UV1 mesh bounding box
                u0 = max(0, int(uv1[:, 0].min() * mw) - 1)
                u1 = min(mw - 1, int(uv1[:, 0].max() * mw) + 1)
                v0 = max(0, int(uv1[:, 1].min() * mh) - 1)
                v1 = min(mh - 1, int(uv1[:, 1].max() * mh) + 1)
                pu_grid, pv_grid = np.meshgrid(
                    np.arange(u0, u1 + 1), np.arange(v0, v1 + 1))
                grid_pts = np.stack(
                    [pu_grid.ravel().astype(np.float32),
                     pv_grid.ravel().astype(np.float32)], axis=1)

                # Nearest vertex for every grid pixel
                _, nn_idx = tree.query(grid_pts)
                nn_uv2 = uv2[nn_idx]  # (M, 2)

                # TM coords = (pixel UV1 - nearest UV2) % 1.0
                grid_u1 = grid_pts[:, 0] / mw
                grid_v1 = grid_pts[:, 1] / mh
                tm_qu = ((grid_u1 - nn_uv2[:, 0]) % 1.0 * mw).astype(int).clip(0, mw - 1)
                tm_qv = ((grid_v1 - nn_uv2[:, 1]) % 1.0 * mh).astype(int).clip(0, mh - 1)
                tm_samples = arr[tm_qv, tm_qu]  # (M, 3)

                # Fill result: default zone 1, then write per-pixel zone
                result = np.zeros((mh, mw, 4), dtype=np.uint8)
                result[:, :, 3] = 255
                if self._zone_colors:
                    zr1, zg1, zb1 = self._zone_colors[0]
                    result[:, :, 0] = zr1
                    result[:, :, 1] = zg1
                    result[:, :, 2] = zb1

                for zone_idx in range(min(3, len(self._zone_colors))):
                    chan  = zone_idx
                    other = [c for c in (0, 1, 2) if c != chan]
                    pmask = tm_samples[:, chan] > THRESH
                    for oc in other:
                        pmask &= tm_samples[:, oc] < LOW
                    if not pmask.any():
                        continue
                    zr, zg, zb = self._zone_colors[zone_idx]
                    cu = pu_grid.ravel()[pmask]
                    cv = pv_grid.ravel()[pmask]
                    result[cv, cu, 0] = zr
                    result[cv, cu, 1] = zg
                    result[cv, cu, 2] = zb

            except Exception:
                # Fallback: per-vertex stamp if scipy unavailable or fails
                tm_u = ((uv1[:, 0] - uv2[:, 0]) % 1.0 * mw).astype(int).clip(0, mw - 1)
                tm_v = ((uv1[:, 1] - uv2[:, 1]) % 1.0 * mh).astype(int).clip(0, mh - 1)
                tm_samples_v = arr[tm_v, tm_u]
                dest_u = (uv1[:, 0] * mw).astype(int).clip(0, mw - 1)
                dest_v = (uv1[:, 1] * mh).astype(int).clip(0, mh - 1)
                result = np.zeros((mh, mw, 4), dtype=np.uint8)
                result[:, :, 3] = 255
                if self._zone_colors:
                    zr1, zg1, zb1 = self._zone_colors[0]
                    result[:, :, 0] = zr1; result[:, :, 1] = zg1; result[:, :, 2] = zb1
                for zone_idx in range(min(3, len(self._zone_colors))):
                    chan = zone_idx; other = [c for c in (0, 1, 2) if c != chan]
                    vmask = tm_samples_v[:, chan] > THRESH
                    for oc in other:
                        vmask &= tm_samples_v[:, oc] < LOW
                    if not vmask.any():
                        continue
                    zr, zg, zb = self._zone_colors[zone_idx]
                    pu = dest_u[vmask]; pv = dest_v[vmask]
                    for dy in (-1, 0, 1):
                        for dx in (-1, 0, 1):
                            result[(pv+dy).clip(0,mh-1), (pu+dx).clip(0,mw-1), 0] = zr
                            result[(pv+dy).clip(0,mh-1), (pu+dx).clip(0,mw-1), 1] = zg
                            result[(pv+dy).clip(0,mh-1), (pu+dx).clip(0,mw-1), 2] = zb

            w, h = mw, mh
            if self._detail_arr is not None:
                from PIL import Image as _PILImg
                darr, dw, dh = self._detail_arr
                if (dw, dh) != (w, h):
                    tmp = _PILImg.fromarray(darr, "RGBA").resize((w, h), _PILImg.LANCZOS)
                    darr = np.array(tmp, dtype=np.uint8)
                a = darr[:, :, 3:4].astype(np.float32) / 255.0
                result[:, :, :3] = np.clip(
                    darr[:, :, :3].astype(np.float32) * a
                    + result[:, :, :3].astype(np.float32) * (1.0 - a),
                    0, 255
                ).astype(np.uint8)
            if self._material_overlay is not None:
                from PIL import Image as _PILImg
                marr, mow, moh = self._material_overlay
                if (mow, moh) != (w, h):
                    tmp = _PILImg.fromarray(marr, "RGBA").resize((w, h), _PILImg.LANCZOS)
                    marr = np.array(tmp, dtype=np.uint8)
                a = marr[:, :, 3:4].astype(np.float32) / 255.0
                result[:, :, :3] = np.clip(
                    marr[:, :, :3].astype(np.float32) * a
                    + result[:, :, :3].astype(np.float32) * (1.0 - a),
                    0, 255
                ).astype(np.uint8)
            return (np.ascontiguousarray(result), w, h)

        # Standard path: pixel-based zone mask lookup (no UV2 correction)
        result = None
        w = h = 0
        for file_idx, mask in enumerate(self._zone_masks):
            if mask is None:
                continue
            arr, mw, mh = mask
            if result is None:
                result = np.zeros((mh, mw, 4), dtype=np.uint8)
                result[:, :, 3] = 255
                w, h = mw, mh
            for chan_idx, chan in enumerate((0, 1, 2)):
                zone_idx = file_idx * 3 + chan_idx
                if zone_idx >= len(self._zone_colors):
                    break
                zr, zg, zb = self._zone_colors[zone_idx]
                other = [c for c in (0, 1, 2) if c != chan]
                mask_px = (arr[:, :, chan] > THRESH)
                for oc in other:
                    mask_px &= (arr[:, :, oc] < LOW)
                result[mask_px, 0] = zr
                result[mask_px, 1] = zg
                result[mask_px, 2] = zb
        if result is None:
            return None
        # Composite detail overlay (dm) on top using alpha blending
        if self._detail_arr is not None:
            from PIL import Image as _PILImg
            darr, dw, dh = self._detail_arr
            if (dw, dh) != (w, h):
                tmp = _PILImg.fromarray(darr, "RGBA").resize((w, h), _PILImg.LANCZOS)
                darr = np.array(tmp, dtype=np.uint8)
            a = darr[:, :, 3:4].astype(np.float32) / 255.0
            result[:, :, :3] = np.clip(
                darr[:, :, :3].astype(np.float32) * a
                + result[:, :, :3].astype(np.float32) * (1.0 - a),
                0, 255
            ).astype(np.uint8)
        return (np.ascontiguousarray(result), w, h)

    def _load_model_async(self):
        cfg        = _EQUIP_CONFIG.get(self._eq_type, _EQUIP_CONFIG[5])
        label      = self._equip_name
        self._set_status(f"Loading {label} model…")

        def _work():
            try:
                import io
                import numpy as np
                from PIL import Image

                # Support single path or list of paths — combine all geometry
                rx2_paths = cfg["rx2"] if isinstance(cfg["rx2"], list) else [cfg["rx2"]]
                all_verts, all_uvs, all_tris, all_uv2s = [], [], [], []
                for rx2_path in rx2_paths:
                    if not os.path.exists(rx2_path):
                        continue
                    with open(rx2_path, "rb") as fh:
                        rx2_data = fh.read()
                    v, u, t, u2 = _te._parse_rx2(rx2_data)
                    if len(v) == 0:
                        continue
                    base = sum(len(x) for x in all_verts)
                    all_verts.append(v)
                    all_uvs.append(u)
                    all_uv2s.append(u2)
                    all_tris.append(t + base)
                if not all_verts:
                    self.after(0, lambda: self._set_status("No geometry found in model."))
                    return
                verts = np.concatenate(all_verts)
                uvs   = np.concatenate(all_uvs)
                uv2s  = np.concatenate(all_uv2s)
                tris  = np.concatenate(all_tris)
                norms = _te._compute_smooth_normals(verts, tris)

                # Load zone-mask rpsgl files (R=zoneA, G=zoneB, B=zoneC per file)
                zone_masks = []
                for rpsgl_path in cfg["zone_masks"]:
                    if not os.path.exists(rpsgl_path):
                        zone_masks.append(None)
                        continue
                    raw      = _te._load_file(rpsgl_path)
                    names    = _te._scan_texture_names(raw)
                    textures = _te._parse_textures(raw, names)
                    if textures:
                        img = Image.open(io.BytesIO(textures[0].to_dds())).convert("RGB")
                        arr = np.array(img, dtype=np.uint8)
                        zone_masks.append((arr, img.width, img.height))
                    else:
                        zone_masks.append(None)

                # Load optional detail/dm overlay
                detail_arr = None
                detail_path = cfg.get("detail")
                if detail_path and os.path.exists(detail_path):
                    raw      = _te._load_file(detail_path)
                    names    = _te._scan_texture_names(raw)
                    textures = _te._parse_textures(raw, names)
                    if textures:
                        img = Image.open(io.BytesIO(textures[0].to_dds())).convert("RGBA")
                        detail_arr = (np.array(img, dtype=np.uint8), img.width, img.height)

                # Load brand-specific material RPSGL (may contain logo overlay texture)
                material_overlay = None
                material_path = cfg.get("material_rpsgl")
                if material_path and os.path.exists(material_path):
                    raw      = _te._load_file(material_path)
                    names    = _te._scan_texture_names(raw)
                    textures = _te._parse_textures(raw, names)
                    if textures:
                        img = Image.open(io.BytesIO(textures[0].to_dds())).convert("RGBA")
                        material_overlay = (np.array(img, dtype=np.uint8), img.width, img.height)

                # Load normal map
                nm_arr = None
                nm_path = cfg.get("nm_rpsgl")
                if nm_path and os.path.exists(nm_path):
                    raw   = _te._load_file(nm_path)
                    names = _te._scan_texture_names(raw)
                    texs  = _te._parse_textures(raw, names)
                    if texs:
                        img = Image.open(io.BytesIO(texs[0].to_dds())).convert("RGBA")
                        nm_arr = (np.array(img, dtype=np.uint8), img.width, img.height)

                # Load specular map
                sm_arr = None
                sm_path = cfg.get("sm_rpsgl")
                if sm_path and os.path.exists(sm_path):
                    raw   = _te._load_file(sm_path)
                    names = _te._scan_texture_names(raw)
                    texs  = _te._parse_textures(raw, names)
                    if texs:
                        img = Image.open(io.BytesIO(texs[0].to_dds())).convert("RGBA")
                        sm_arr = (np.array(img, dtype=np.uint8), img.width, img.height)

                mins   = verts.min(axis=0)
                maxs   = verts.max(axis=0)
                center = (mins + maxs) * 0.5
                span   = float((maxs - mins).max()) or 1.0
                valid  = tris.max(axis=1) < len(verts)
                tris   = tris[valid]
                has_uv2 = cfg.get("tm_uses_uv2_offset", False) and uv2s is not None

                def _show():
                    if not self.winfo_exists() or not self._gl_view:
                        return
                    self._zone_masks       = zone_masks
                    self._detail_arr       = detail_arr
                    self._material_overlay = material_overlay
                    self._mesh_uv1         = uvs  if has_uv2 else None
                    self._mesh_uv2         = uv2s if has_uv2 else None
                    self._gl_view.set_mesh(verts, norms, uvs, tris, center, span)
                    uv_v = cfg.get("uv_v_offset", 0.0)
                    if uv_v:
                        self._gl_view.set_uv_offset(0.0, uv_v)
                    tex_dict = {}
                    colored = self._build_colored_texture()
                    if colored is not None:
                        tex_dict["cm"] = colored
                    if nm_arr is not None:
                        tex_dict["nm"] = nm_arr
                    if sm_arr is not None:
                        tex_dict["sm"] = sm_arr
                    if tex_dict:
                        self._gl_view.set_textures(tex_dict)
                    masks_ok = sum(1 for m in zone_masks if m is not None)
                    self._set_status(
                        f"{label}  —  {len(verts):,} verts · {len(tris):,} tris"
                        f"  |  {masks_ok} zone mask(s)  |  drag to rotate · scroll to zoom"
                    )

                self.after(0, _show)

            except Exception as exc:
                self.after(0, lambda e=exc: self._set_status(f"Error loading model: {e}"))

        threading.Thread(target=_work, daemon=True).start()

    def update_zone_colors(self, zone_colors):
        """Refresh legend swatches, RGB labels, and 3-D texture with updated zone colours."""
        self._zone_colors = list(zone_colors)
        for z, (sw, val_lbl) in enumerate(zip(self._swatch_labels, self._val_labels)):
            if z >= len(zone_colors):
                break
            r, g, b = zone_colors[z]
            hex_col = f"#{r:02X}{g:02X}{b:02X}"
            sw.configure(bg=hex_col)
            val_lbl.configure(text=f"R:{r}  G:{g}  B:{b}  {hex_col.upper()}")
        if self._zone_masks and self._gl_view:
            colored = self._build_colored_texture()
            if colored is not None:
                self._gl_view.set_textures({"cm": colored})


# ── Main App ───────────────────────────────────────────────────────────────────

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Heck's Jersey DB Editor")
        self.geometry("1100x800"); self.minsize(900, 600)
        self.configure(bg="#050505")

        self.db: EADatabase | None = None
        self.dirty = False

        # ── Equipment tab state
        self.eq_table:       EATable | None = None
        self.eq_current_idx: int | None     = None
        self._visible:       list[int]      = []
        self._suppress = False
        self.info_vars:       dict[str, tk.StringVar] = {}
        self.zone_vars:       list[list[tk.StringVar]] = []
        self.swatches:        list[tk.Label] = []
        self.zone_labels:     list[tk.Label] = []
        self.zone_prev_labels: list[tk.Label] = []

        # ── Undo stack — each entry: (record_idx, raw_bytes_before_edit)
        self._undo_stack: list[tuple[int, bytes]] = []
        self._eq_snapshot: tuple[int, bytes] | None = None  # pre-edit snapshot of loaded row
        self._eq_modified: set[int] = set()  # record indices changed since last save

        # ── Equipment preview window
        self._preview_win: "EquipPreviewWindow | None" = None

        # ── Lang tab state
        self.lang_table:        EATable | None = None
        self._lang_meta_fields: dict           = {}   # {shortname: longname}
        self._lang_all_rows:    list           = []   # [(record_idx, field1, field2, ...)]
        self._lang_tree:        None           = None
        self._lang_search:      tk.StringVar | None = None
        self._lang_meta_label:  None           = None
        self._lang_count_label: None           = None
        # Bridge mode (little-endian / Huffman lang DBs)
        self._lang_bridge_path:   str | None = None  # path to currently-loaded lang DB
        self._lang_bridge_fields: list       = []    # field shortnames in bridge order
        self._lang_pending_edits: list       = []    # [{index, field, value}, ...]
        # Xbox (big-endian) lang DB state
        self._lang_is_xbox:       bool       = False
        self._lang_xbox_src_path: str | None = None  # original Xbox file path
        self._lang_temp_path:     str | None = None  # temp PC-format file

        # ── Team Settings tab state
        self.ts_table:         EATable | None = None
        self.ts_current_idx:   int | None     = None
        self._ts_visible:      list[int]      = []
        self._ts_suppress:     bool           = False
        self.ts_vars:          dict[str, tk.StringVar] = {}
        self._ts_undo_stack:   list[tuple[int, bytes]] = []
        self._ts_snapshot:     tuple[int, bytes] | None = None
        self._ts_modified:     set[int]        = set()
        self.ts_color1_swatch: tk.Label | None = None
        self.ts_color2_swatch: tk.Label | None = None

        # ── Jersey tab state
        self.jy_table:       EATable | None = None
        self.jy_current_idx: int | None     = None
        self._jy_visible:    list[int]      = []
        self._jy_suppress = False
        self.jy_vars:    dict[str, tk.StringVar] = {}

        self._build_ui()

    # ── UI ─────────────────────────────────────────────────────────────────────

    # Black & light-blue palette
    BG      = "#050505"
    PANEL   = "#0c0c0c"
    SURF    = "#111111"
    LIFT    = "#1c1c1c"
    BORDER  = "#2a2a2a"
    DIM     = "#444444"
    SUB     = "#7a8a99"
    TEXT    = "#dce8f0"
    ACCENT  = "#38bdf8"   # sky blue
    BLUE    = "#7dd3fc"   # lighter sky blue
    GREEN   = "#4ade80"
    RED     = "#f87171"
    YELLOW  = "#fbbf24"

    def _section_header(self, parent, title, bg=None):
        """Clean section header: accent left bar + bold label + horizontal rule."""
        bg = bg or self.SURF
        row = tk.Frame(parent, bg=bg)
        row.pack(fill=tk.X, padx=10, pady=(10, 2))
        tk.Frame(row, bg=self.ACCENT, width=3).pack(side=tk.LEFT, fill=tk.Y)
        tk.Label(row, text=f"  {title}", bg=bg, fg=self.ACCENT,
                 font=("Segoe UI", 9, "bold")).pack(side=tk.LEFT, pady=4)
        tk.Frame(parent, bg=self.BORDER, height=1).pack(fill=tk.X, padx=10, pady=(0, 6))

    def _build_ui(self):
        s = ttk.Style(self); s.theme_use("clam")

        # Frames
        s.configure("TFrame",        background=self.SURF)
        s.configure("TPanedwindow",  background=self.BORDER)
        s.configure("TSeparator",    background=self.BORDER)

        # Labels
        s.configure("TLabel",       background=self.SURF,  foreground=self.TEXT,   font=("Segoe UI", 9))
        s.configure("H.TLabel",     background=self.SURF,  foreground=self.ACCENT, font=("Segoe UI", 9, "bold"))
        s.configure("Dim.TLabel",   background=self.SURF,  foreground=self.DIM,    font=("Consolas", 7))
        s.configure("Hint.TLabel",  background=self.SURF,  foreground=self.DIM,    font=("Segoe UI", 8))
        s.configure("Panel.TLabel", background=self.PANEL, foreground=self.TEXT,   font=("Segoe UI", 9))

        # Buttons — flat, modern
        s.configure("TButton",
                    background=self.LIFT, foreground=self.TEXT,
                    relief="flat", padding=(10, 5), font=("Segoe UI", 9), borderwidth=0)
        s.map("TButton",
              background=[("active", self.BORDER), ("pressed", self.DIM)])

        s.configure("Accent.TButton",
                    background=self.BLUE, foreground=self.BG,
                    relief="flat", padding=(10, 5), font=("Segoe UI", 9, "bold"), borderwidth=0)
        s.map("Accent.TButton",
              background=[("active", "#7baff5"), ("pressed", "#6b9fe5")])

        s.configure("Danger.TButton",
                    background="#3d1c24", foreground=self.RED,
                    relief="flat", padding=(10, 5), font=("Segoe UI", 9), borderwidth=0)
        s.map("Danger.TButton",
              background=[("active", "#5a2530"), ("pressed", "#4a1c26")])

        s.configure("Small.TButton",
                    background=self.LIFT, foreground=self.SUB,
                    relief="flat", padding=(6, 3), font=("Segoe UI", 8), borderwidth=0)
        s.map("Small.TButton",
              background=[("active", self.BORDER)])

        # Entries
        s.configure("TEntry",
                    fieldbackground=self.LIFT, foreground=self.TEXT,
                    insertcolor=self.TEXT, borderwidth=0, relief="flat", padding=(6, 4))

        # Notebook
        s.configure("TNotebook",     background=self.PANEL, borderwidth=0, tabmargins=[0, 0, 0, 0])
        s.configure("TNotebook.Tab", background=self.LIFT,  foreground=self.SUB,
                    padding=[18, 8], font=("Segoe UI", 9))
        s.map("TNotebook.Tab",
              background=[("selected", self.SURF)],
              foreground=[("selected", self.TEXT)],
              expand=[("selected", [0, 0, 0, 0])])

        # Scrollbar
        s.configure("TScrollbar",
                    background=self.LIFT, troughcolor=self.PANEL,
                    borderwidth=0, arrowsize=10, relief="flat")
        s.map("TScrollbar", background=[("active", self.BORDER)])

        # Spinbox
        s.configure("TSpinbox",
                    fieldbackground=self.LIFT, foreground=self.TEXT,
                    arrowcolor=self.SUB, borderwidth=0, padding=(6, 4))

        self.configure(bg=self.BG)

        # ── Header bar ──────────────────────────────────────────────────────
        hdr = tk.Frame(self, bg=self.PANEL, height=50)
        hdr.pack(side=tk.TOP, fill=tk.X)
        hdr.pack_propagate(False)

        tk.Label(hdr, text="Heck's Jersey DB Editor", bg=self.PANEL, fg=self.ACCENT,
                 font=("Segoe UI", 12, "bold"), padx=14).pack(side=tk.LEFT, pady=10)
        tk.Frame(hdr, bg=self.BORDER, width=1).pack(side=tk.LEFT, fill=tk.Y, pady=10, padx=2)

        ttk.Button(hdr, text="Open",          command=self.open_db,     style="TButton"      ).pack(side=tk.LEFT, padx=(6,2), pady=10)
        ttk.Button(hdr, text="Save",          command=self.save_db,     style="Accent.TButton").pack(side=tk.LEFT, padx=2,    pady=10)
        ttk.Button(hdr, text="Save As",       command=self.save_db_as,  style="TButton"      ).pack(side=tk.LEFT, padx=2,    pady=10)
        ttk.Button(hdr, text="Backup & Save", command=self.backup_save, style="Small.TButton").pack(side=tk.LEFT, padx=2,    pady=10)

        self.status = tk.StringVar(value="Open a .DB file to begin.")
        tk.Label(hdr, textvariable=self.status, bg=self.PANEL, fg=self.GREEN,
                 font=("Segoe UI", 9)).pack(side=tk.RIGHT, padx=14)

        # Divider under header
        tk.Frame(self, bg=self.BORDER, height=1).pack(fill=tk.X)

        # ── Notebook ────────────────────────────────────────────────────────
        self.nb = ttk.Notebook(self)
        self.nb.pack(fill=tk.BOTH, expand=True)

        eq_frame   = ttk.Frame(self.nb); self.nb.add(eq_frame,   text="  Stock Team Equipment  ")
        jy_frame   = ttk.Frame(self.nb); self.nb.add(jy_frame,   text="  Team Jerseys  ")
        ts_frame   = ttk.Frame(self.nb); self.nb.add(ts_frame,   text="  Team Settings  ")
        lang_frame = ttk.Frame(self.nb); self.nb.add(lang_frame, text="  Language Strings  ")

        self._eq_tab_idx   = 0
        self._jy_tab_idx   = 1
        self._ts_tab_idx   = 2
        self._lang_tab_idx = 3

        self._build_eq_tab(eq_frame)
        self._build_jy_tab(jy_frame)
        self._build_ts_tab(ts_frame)
        self._build_lang_tab(lang_frame)

        self.bind("<Control-z>",       lambda e: self._eq_undo())
        self.bind("<Control-Shift-z>", lambda e: self._ts_undo())

    # ══════════════════════════════════════════════════════════════════════════
    # EQUIPMENT TAB
    # ══════════════════════════════════════════════════════════════════════════

    def _build_eq_tab(self, parent):
        pane = ttk.PanedWindow(parent, orient=tk.HORIZONTAL)
        pane.pack(fill=tk.BOTH, expand=True)

        left = ttk.Frame(pane, width=300); pane.add(left, weight=1)
        self._build_eq_list(left)

        right = ttk.Frame(pane); pane.add(right, weight=4)
        self._build_eq_edit(right)

    def _build_eq_list(self, parent):
        # Sidebar panel
        sidebar = tk.Frame(parent, bg=self.PANEL)
        sidebar.pack(fill=tk.BOTH, expand=True)

        # Header
        head = tk.Frame(sidebar, bg=self.PANEL)
        head.pack(fill=tk.X, padx=10, pady=(10, 4))
        tk.Label(head, text="Records", bg=self.PANEL, fg=self.TEXT,
                 font=("Segoe UI", 11, "bold")).pack(side=tk.LEFT)

        # Search bar
        sf = tk.Frame(sidebar, bg=self.LIFT)
        sf.pack(fill=tk.X, padx=10, pady=(0, 6))
        tk.Label(sf, text=" ⌕ ", bg=self.LIFT, fg=self.SUB,
                 font=("Segoe UI", 10)).pack(side=tk.LEFT)
        self.search = tk.StringVar()
        self.search.trace_add("write", lambda *_: self._eq_refresh_list())
        tk.Entry(sf, textvariable=self.search, bg=self.LIFT, fg=self.TEXT,
                 insertbackground=self.TEXT, relief="flat", font=("Segoe UI", 9),
                 bd=0).pack(side=tk.LEFT, fill=tk.X, expand=True, pady=5, padx=(0, 6))

        # Action buttons — row 1
        bf = tk.Frame(sidebar, bg=self.PANEL)
        bf.pack(fill=tk.X, padx=10, pady=(0, 3))
        ttk.Button(bf, text="Duplicate",    command=self.duplicate_row,    style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf, text="Delete",       command=self._eq_delete_rows,  style="Danger.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf, text="Delete Blank", command=self._eq_delete_blank, style="Danger.TButton").pack(side=tk.LEFT)

        # Action buttons — row 2
        bf2 = tk.Frame(sidebar, bg=self.PANEL)
        bf2.pack(fill=tk.X, padx=10, pady=(0, 6))
        ttk.Button(bf2, text="Export…", command=self._eq_export, style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf2, text="Import…", command=self._eq_import, style="Small.TButton").pack(side=tk.LEFT)

        # Divider
        tk.Frame(sidebar, bg=self.BORDER, height=1).pack(fill=tk.X, padx=10, pady=(0, 4))

        # Listbox (extended = Shift+click range, Ctrl+click individual)
        lf = tk.Frame(sidebar, bg=self.PANEL)
        lf.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))
        self.lb = tk.Listbox(lf, bg=self.PANEL, fg=self.TEXT,
                             selectbackground=self.LIFT, selectforeground=self.ACCENT,
                             activestyle="none", relief="flat", bd=0,
                             font=("Consolas", 9), exportselection=False,
                             selectmode="extended", highlightthickness=0)
        sc = ttk.Scrollbar(lf, command=self.lb.yview)
        self.lb.configure(yscrollcommand=sc.set)
        sc.pack(side=tk.RIGHT, fill=tk.Y)
        self.lb.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.lb.bind("<<ListboxSelect>>", self._eq_on_select)

    def _build_eq_edit(self, parent):
        canvas = tk.Canvas(parent, bg=self.SURF, highlightthickness=0)
        vs = ttk.Scrollbar(parent, orient=tk.VERTICAL, command=canvas.yview)
        canvas.configure(yscrollcommand=vs.set)
        vs.pack(side=tk.RIGHT, fill=tk.Y)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        inner = tk.Frame(canvas, bg=self.SURF)
        win   = canvas.create_window((0,0), window=inner, anchor="nw")
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(win, width=e.width))
        canvas.bind_all("<MouseWheel>",
                        lambda e: canvas.yview_scroll(-1*(e.delta//120), "units"))

        # ── Equipment Info section ───────────────────────────────────────────
        self._section_header(inner, "Equipment Info")
        ig = tk.Frame(inner, bg=self.SURF)
        ig.pack(fill=tk.X, padx=16, pady=(0, 10))
        for idx, abbrev in enumerate(INFO_FIELDS):
            col, row = idx % 3, idx // 3
            cell = tk.Frame(ig, bg=self.SURF)
            cell.grid(row=row, column=col, padx=10, pady=4, sticky="w")
            label = FIELD_MAP.get(abbrev, abbrev)
            tk.Label(cell, text=f"{label}", bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
            tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                     font=("Segoe UI", 7)).pack(anchor=tk.W)
            var = tk.StringVar()
            var.trace_add("write", self._eq_on_change)
            self.info_vars[abbrev] = var
            ttk.Entry(cell, textvariable=var, width=14).pack(anchor=tk.W, pady=(2, 0))

        # ── Zone Colors section ──────────────────────────────────────────────
        self._section_header(inner, "Zone Colors")

        # Column headers
        ch = tk.Frame(inner, bg=self.SURF)
        ch.pack(fill=tk.X, padx=16, pady=(0, 4))
        tk.Label(ch, text="Zone",       bg=self.SURF, fg=self.SUB, font=("Segoe UI", 8, "bold"), width=22, anchor="w").grid(row=0, column=0)
        tk.Label(ch, text="Red",        bg=self.SURF, fg=self.RED,    font=("Segoe UI", 8, "bold"), width=8).grid(row=0, column=1, padx=2)
        tk.Label(ch, text="Green",      bg=self.SURF, fg=self.GREEN,  font=("Segoe UI", 8, "bold"), width=8).grid(row=0, column=2, padx=2)
        tk.Label(ch, text="Blue",       bg=self.SURF, fg=self.BLUE,   font=("Segoe UI", 8, "bold"), width=8).grid(row=0, column=3, padx=2)
        tk.Label(ch, text="Preview",    bg=self.SURF, fg=self.SUB,    font=("Segoe UI", 8, "bold"), width=8).grid(row=0, column=4, padx=6)

        zg = tk.Frame(inner, bg=self.SURF)
        zg.pack(fill=tk.X, padx=16, pady=(0, 6))

        for z in range(9):
            rv = []
            row_bg = self.PANEL if z % 2 == 0 else self.SURF
            row_frame = tk.Frame(zg, bg=row_bg)
            row_frame.grid(row=z, column=0, columnspan=7, sticky="ew", pady=1)

            zlbl = tk.Label(row_frame, text=f"Zone {z+1}", bg=row_bg, fg=self.ACCENT,
                            font=("Segoe UI", 9, "bold"), width=22, anchor="w", padx=4)
            zlbl.pack(side=tk.LEFT, pady=3)
            self.zone_labels.append(zlbl)

            for c in range(3):
                var = tk.StringVar()
                var.trace_add("write", self._eq_on_change)
                rv.append(var)

            # Visual: R(col1), G(col2), B(col3)
            for col_idx, (var_idx, border_col) in enumerate([(2, self.RED), (1, self.GREEN), (0, self.BLUE)]):
                ef = tk.Frame(row_frame, bg=border_col, padx=1, pady=1)
                ef.pack(side=tk.LEFT, padx=3, pady=3)
                ttk.Entry(ef, textvariable=rv[var_idx], width=6).pack()

            sw = tk.Label(row_frame, text="        ", bg=self.LIFT,
                          width=8, cursor="hand2", relief="flat")
            sw.pack(side=tk.LEFT, padx=(8, 2), pady=3)
            sw.bind("<Button-1>", lambda e, z=z: self._pick(z))
            self.swatches.append(sw)

            tk.Button(row_frame, text="Pick", bg=self.LIFT, fg=self.SUB,
                      relief="flat", font=("Segoe UI", 8), cursor="hand2",
                      activebackground=self.BORDER, activeforeground=self.TEXT,
                      bd=0, padx=6, pady=2,
                      command=lambda z=z: self._pick(z)).pack(side=tk.LEFT, padx=4)

            ab = "  ".join(ZONE_ABBREVS[z])
            tk.Label(row_frame, text=ab, bg=row_bg, fg=self.DIM,
                     font=("Consolas", 7)).pack(side=tk.LEFT, padx=(4, 0))

            prev_lbl = tk.Label(row_frame, text="", bg=row_bg, fg=self.DIM,
                                font=("Consolas", 7))
            prev_lbl.pack(side=tk.LEFT, padx=(10, 4))
            self.zone_prev_labels.append(prev_lbl)

            self.zone_vars.append(rv)

        # Zone action buttons
        br = tk.Frame(inner, bg=self.SURF)
        br.pack(fill=tk.X, padx=16, pady=(4, 6))
        ttk.Button(br, text="Zone 1 → All",           command=self._z1_all,                style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(br, text="Copy Zone",               command=self._copy_zone_dlg,         style="Small.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(br, text="Paste Zone",              command=self._paste_zone_dlg,        style="Small.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(br, text="Apply to Siblings",       command=self._apply_siblings,        style="Small.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(br, text="Copy All Zones to Variant…", command=self._copy_zones_to_variant,    style="Small.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(br, text="Swap Zones Between Variants…", command=self._swap_zones_between_variants, style="Small.TButton").pack(side=tk.LEFT, padx=4)

        br2 = tk.Frame(inner, bg=self.SURF)
        br2.pack(fill=tk.X, padx=16, pady=(0, 16))
        ttk.Button(br2, text="⟲ Undo  (Ctrl+Z)", command=self._eq_undo, style="Small.TButton").pack(side=tk.LEFT)
        ttk.Button(br2, text="Preview 3D…",       command=self._open_eq_preview, style="Small.TButton").pack(side=tk.LEFT, padx=(8, 0))

    # ── Equipment list ─────────────────────────────────────────────────────────

    def _eq_row_label(self, i):
        r = self.eq_table.records[i]
        tid     = r.get("dXSB")
        eq      = r.get("jJpM")
        brand   = r.get("TKWL")
        era     = r.get("VhbR")
        variant = r.get("FccT")
        dirty   = " ×" if i in self._eq_modified else ""
        return f"[{i}] {get_team_name(tid)}  {get_equip_name(eq)}  Brand {brand}  {get_era_name(era)}  Variant {variant}{dirty}"

    def _eq_refresh_list(self):
        ft = self.search.get().lower()
        self.lb.delete(0, tk.END); self._visible = []
        if not self.eq_table: return
        def sort_key(i):
            r = self.eq_table.records[i]
            try: tid     = int(r.get("dXSB") or 0)
            except: tid = 0
            try: eq      = int(r.get("jJpM") or 0)
            except: eq = 0
            try: era     = int(r.get("VhbR") or 0)
            except: era = 0
            try: variant = int(r.get("FccT") or 0)
            except: variant = 0
            return (tid, eq, era, variant)
        indices = sorted(range(len(self.eq_table.records)), key=sort_key)
        for i in indices:
            label = self._eq_row_label(i)
            if ft and ft not in label.lower(): continue
            self.lb.insert(tk.END, label); self._visible.append(i)

    def _eq_on_select(self, _):
        sel = self.lb.curselection()
        if not sel: return
        # Only populate the edit panel when exactly one row is selected
        if len(sel) != 1: return
        lp = sel[0]
        if lp >= len(self._visible): return
        self._eq_load_row(self._visible[lp])

    def _eq_load_row(self, idx):
        self.eq_current_idx = idx
        rec = self.eq_table.records[idx]
        eq_raw = rec.get("jJpM")
        self._last_eq_raw = eq_raw

        # Snapshot for undo (one per row load; pushed to stack on first edit)
        self._eq_snapshot = (idx, bytes(rec.raw))

        self._suppress = True
        for abbrev, var in self.info_vars.items():
            var.set(rec.get(abbrev))
        for z, rv in enumerate(self.zone_vars):
            r_str = rec.get(ZONE_ABBREVS[z][2])
            g_str = rec.get(ZONE_ABBREVS[z][1])
            b_str = rec.get(ZONE_ABBREVS[z][0])
            rv[0].set(b_str); rv[1].set(g_str); rv[2].set(r_str)
            self._update_zone_swatch(z)
            self.zone_labels[z].configure(text=get_zone_name(eq_raw, z))
            # Show "was" values
            if self.zone_prev_labels:
                self.zone_prev_labels[z].configure(
                    text=f"← was  R:{r_str}  G:{g_str}  B:{b_str}")
        self._suppress = False

    def _push_eq_undo_if_needed(self):
        """Push pre-edit snapshot to undo stack (only once per row load)."""
        if self._eq_snapshot is not None:
            self._undo_stack.append(self._eq_snapshot)
            self._eq_modified.add(self._eq_snapshot[0])
            self._eq_snapshot = None

    def _eq_refresh_label(self, idx):
        """Refresh a single listbox label (e.g. to show/hide ×)."""
        try:
            lp = self._visible.index(idx)
            self.lb.delete(lp)
            self.lb.insert(lp, self._eq_row_label(idx))
            if idx == self.eq_current_idx:
                self.lb.selection_set(lp)
        except ValueError:
            pass

    def _eq_undo(self):
        if not self._undo_stack:
            self.status.set("Nothing to undo."); return
        idx, raw = self._undo_stack.pop()
        self.eq_table.records[idx].raw[:] = bytearray(raw)
        self._eq_modified.discard(idx)
        # Reload display (also re-snapshots, which is fine)
        self._eq_load_row(idx)
        try:
            lp = self._visible.index(idx)
            self.lb.delete(lp); self.lb.insert(lp, self._eq_row_label(idx))
            self.lb.selection_clear(0, tk.END)
            self.lb.selection_set(lp); self.lb.see(lp)
        except ValueError:
            pass
        self.dirty = True
        self.status.set(f"Undo: record {idx} restored.")

    def _open_eq_preview(self):
        """Open or focus the 3-D equipment preview window."""
        if self.eq_current_idx is None:
            messagebox.showinfo("No record", "Select an equipment record first.")
            return
        rec = self.eq_table.records[self.eq_current_idx]
        eq_raw = rec.get("jJpM")
        try:
            eq_type = int(eq_raw)
        except (TypeError, ValueError):
            eq_type = 5
        zone_colors = [
            (
                _channel_byte(rec.get(ZONE_ABBREVS[z][2])),
                _channel_byte(rec.get(ZONE_ABBREVS[z][1])),
                _channel_byte(rec.get(ZONE_ABBREVS[z][0])),
            )
            for z in range(9)
        ]
        zone_names = [get_zone_name(eq_raw, z) for z in range(9)]
        equip_name = get_equip_name(eq_raw)

        win = self._preview_win
        if win is not None and win.winfo_exists():
            if win._eq_type != eq_type:
                win.destroy()
                self._preview_win = EquipPreviewWindow(
                    self, zone_colors, zone_names, equip_name, eq_type=eq_type)
            else:
                win.update_zone_colors(zone_colors)
                win.lift()
        else:
            self._preview_win = EquipPreviewWindow(
                self, zone_colors, zone_names, equip_name, eq_type=eq_type)

    def _eq_on_change(self, *_):
        if self._suppress or self.eq_current_idx is None: return
        self._push_eq_undo_if_needed()
        rec = self.eq_table.records[self.eq_current_idx]
        eq_raw = self.info_vars.get("jJpM", tk.StringVar()).get()

        for abbrev, var in self.info_vars.items():
            rec.set(abbrev, var.get())
        for z, rv in enumerate(self.zone_vars):
            for c, var in enumerate(rv):
                rec.set(ZONE_ABBREVS[z][c], var.get())
            self._update_zone_swatch(z)
        if eq_raw != getattr(self, "_last_eq_raw", None):
            self._last_eq_raw = eq_raw
            for z in range(9):
                self.zone_labels[z].configure(text=get_zone_name(eq_raw, z))

        self.dirty = True
        try:
            lp = self._visible.index(self.eq_current_idx)
            self.lb.delete(lp); self.lb.insert(lp, self._eq_row_label(self.eq_current_idx))
            self.lb.selection_set(lp)
        except ValueError:
            pass

        if self._preview_win is not None and self._preview_win.winfo_exists():
            zone_colors = [
                (
                    _channel_byte(self.zone_vars[z][2].get()),
                    _channel_byte(self.zone_vars[z][1].get()),
                    _channel_byte(self.zone_vars[z][0].get()),
                )
                for z in range(9)
            ]
            self._preview_win.update_zone_colors(zone_colors)

    def _update_zone_swatch(self, z):
        try:
            b = _channel_byte(self.zone_vars[z][0].get())
            g = _channel_byte(self.zone_vars[z][1].get())
            r = _channel_byte(self.zone_vars[z][2].get())
            color = f"#{r:02X}{g:02X}{b:02X}"
            lum   = 0.299*r + 0.587*g + 0.114*b
            self.swatches[z].configure(bg=color, fg="#000" if lum>128 else "#fff")
        except Exception:
            self.swatches[z].configure(bg=self.LIFT, fg=self.TEXT)

    # ── Equipment zone utilities ───────────────────────────────────────────────

    def _pick(self, z):
        try:
            b = _channel_byte(self.zone_vars[z][0].get())
            g = _channel_byte(self.zone_vars[z][1].get())
            r = _channel_byte(self.zone_vars[z][2].get())
            init = f"#{r:02X}{g:02X}{b:02X}"
        except Exception:
            init = "#000000"
        result = colorchooser.askcolor(color=init,
                                       title=f"Zone {z+1} — {self.zone_labels[z].cget('text')}")
        if result and result[0]:
            self._push_eq_undo_if_needed()
            ri, gi, bi = [int(x) for x in result[0]]
            self._suppress = True
            self.zone_vars[z][0].set(str(bi))
            self.zone_vars[z][1].set(str(gi))
            self.zone_vars[z][2].set(str(ri))
            self._suppress = False
            if self.eq_current_idx is not None:
                rec = self.eq_table.records[self.eq_current_idx]
                for c in range(3):
                    rec.set(ZONE_ABBREVS[z][c], self.zone_vars[z][c].get())
                self._update_zone_swatch(z)
                self.dirty = True
                self._eq_refresh_label(self.eq_current_idx)

    def _z1_all(self):
        if self.eq_current_idx is None: return
        self._push_eq_undo_if_needed()
        vals = [self.zone_vars[0][c].get() for c in range(3)]
        self._suppress = True
        rec = self.eq_table.records[self.eq_current_idx]
        for z in range(1, 9):
            for c in range(3):
                self.zone_vars[z][c].set(vals[c])
                rec.set(ZONE_ABBREVS[z][c], vals[c])
            self._update_zone_swatch(z)
        self._suppress = False
        self.dirty = True
        self._eq_refresh_label(self.eq_current_idx)

    def _copy_zone_dlg(self):
        z = self._ask_zone("Copy zone number (1-9):")
        if z is None: return
        self._clipboard = [self.zone_vars[z][c].get() for c in range(3)]
        self.status.set(f"Zone {z+1} ({self.zone_labels[z].cget('text')}) copied.")

    def _paste_zone_dlg(self):
        if not hasattr(self, "_clipboard"):
            messagebox.showinfo("Nothing copied", "Copy a zone first."); return
        z = self._ask_zone("Paste into zone number (1-9):")
        if z is None: return
        self._push_eq_undo_if_needed()
        self._suppress = True
        rec = self.eq_table.records[self.eq_current_idx]
        for c in range(3):
            self.zone_vars[z][c].set(self._clipboard[c])
            rec.set(ZONE_ABBREVS[z][c], self._clipboard[c])
        self._update_zone_swatch(z)
        self._suppress = False
        self.dirty = True
        self._eq_refresh_label(self.eq_current_idx)

    def _ask_zone(self, prompt):
        dlg = tk.Toplevel(self); dlg.title("Zone"); dlg.grab_set()
        dlg.configure(bg=self.SURF); dlg.resizable(False, False)
        tk.Label(dlg, text=prompt, bg=self.SURF, fg=self.TEXT,
                 font=("Segoe UI", 9)).pack(padx=20, pady=(16, 6))
        var = tk.IntVar(value=1)
        ttk.Spinbox(dlg, from_=1, to=9, textvariable=var, width=8).pack(padx=20, pady=4)
        result = [None]
        def ok(): result[0] = var.get()-1; dlg.destroy()
        ttk.Button(dlg, text="OK", command=ok, style="Accent.TButton").pack(pady=(8, 16))
        self.wait_window(dlg)
        return result[0]

    def _apply_siblings(self):
        if self.eq_current_idx is None or not self.eq_table: return
        self._push_eq_undo_if_needed()
        src = self.eq_table.records[self.eq_current_idx]
        eq  = src.get("jJpM"); count = 0
        for i, rec in enumerate(self.eq_table.records):
            if i == self.eq_current_idx or rec.get("jJpM") != eq: continue
            self._undo_stack.append((i, bytes(rec.raw)))
            self._eq_modified.add(i)
            for z in range(9):
                for c in range(3):
                    rec.set(ZONE_ABBREVS[z][c], src.get(ZONE_ABBREVS[z][c]))
            self._eq_refresh_label(i)
            count += 1
        self.dirty = True
        self.status.set(f"Zone colors copied to {count} sibling(s) with {get_equip_name(eq)}.")

    def _copy_zones_to_variant(self):
        if self.eq_current_idx is None or not self.eq_table: return
        src = self.eq_table.records[self.eq_current_idx]
        src_tid = src.get("dXSB")
        src_eq  = src.get("jJpM")
        src_era = src.get("VhbR")
        src_var = src.get("FccT")

        dlg = tk.Toplevel(self); dlg.title("Copy All Zones to Variant"); dlg.grab_set()
        dlg.configure(bg=self.SURF); dlg.resizable(False, False)

        tk.Label(dlg, text=f"Copy all zone colors from Variant {src_var}",
                 bg=self.SURF, fg=self.TEXT, font=("Segoe UI", 9, "bold")).pack(padx=24, pady=(16, 2))
        tk.Label(dlg, text=f"Team {get_team_name(src_tid)}  ·  {get_equip_name(src_eq)}  ·  {get_era_name(src_era)}",
                 bg=self.SURF, fg=self.SUB, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 8))
        tk.Label(dlg, text="Target variant number:",
                 bg=self.SURF, fg=self.TEXT, font=("Segoe UI", 9)).pack(padx=24, pady=(0, 4))

        var = tk.IntVar(value=0)
        ttk.Spinbox(dlg, from_=0, to=31, textvariable=var, width=10).pack(padx=24, pady=(0, 12))

        result = [None]
        def ok(): result[0] = var.get(); dlg.destroy()
        bf = tk.Frame(dlg, bg=self.SURF); bf.pack(pady=(0, 16))
        ttk.Button(bf, text="Copy Zones", command=ok,          style="Accent.TButton").pack(side=tk.LEFT, padx=(0,6))
        ttk.Button(bf, text="Cancel",     command=dlg.destroy, style="Small.TButton" ).pack(side=tk.LEFT)
        self.wait_window(dlg)
        if result[0] is None: return

        target_var = str(result[0])
        target_rec = None; target_idx = None
        for i, rec in enumerate(self.eq_table.records):
            if (rec.get("dXSB") == src_tid and rec.get("jJpM") == src_eq and
                    rec.get("VhbR") == src_era and rec.get("FccT") == target_var):
                target_rec = rec; target_idx = i; break

        if target_rec is None:
            messagebox.showerror("Not found",
                f"No record found matching Team {get_team_name(src_tid)}, "
                f"{get_equip_name(src_eq)}, {get_era_name(src_era)}, Variant {target_var}.")
            return

        # Push undo for both source (if pending) and target
        self._push_eq_undo_if_needed()
        self._undo_stack.append((target_idx, bytes(target_rec.raw)))
        self._eq_modified.add(target_idx)

        for z in range(9):
            for c in range(3):
                target_rec.set(ZONE_ABBREVS[z][c], src.get(ZONE_ABBREVS[z][c]))

        self._eq_refresh_label(target_idx)
        self.dirty = True
        self.status.set(
            f"All zone colors copied from Variant {src_var} → Variant {target_var} "
            f"({get_equip_name(src_eq)}, {get_era_name(src_era)}).")

    def _swap_zones_between_variants(self):
        if self.eq_current_idx is None or not self.eq_table: return
        src = self.eq_table.records[self.eq_current_idx]
        src_tid = src.get("dXSB")
        src_eq  = src.get("jJpM")
        src_era = src.get("VhbR")
        src_var = src.get("FccT")

        dlg = tk.Toplevel(self); dlg.title("Swap Zones Between Variants"); dlg.grab_set()
        dlg.configure(bg=self.SURF); dlg.resizable(False, False)

        tk.Label(dlg, text="Swap all zone colors between two variants",
                 bg=self.SURF, fg=self.TEXT, font=("Segoe UI", 9, "bold")).pack(padx=24, pady=(16, 2))
        tk.Label(dlg, text=f"Team {get_team_name(src_tid)}  ·  {get_equip_name(src_eq)}  ·  {get_era_name(src_era)}",
                 bg=self.SURF, fg=self.SUB, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 10))

        row_a = tk.Frame(dlg, bg=self.SURF); row_a.pack(padx=24, fill=tk.X, pady=2)
        tk.Label(row_a, text="Variant A:", bg=self.SURF, fg=self.TEXT,
                 font=("Segoe UI", 9), width=12, anchor="w").pack(side=tk.LEFT)
        var_a = tk.IntVar(value=int(src_var) if src_var.isdigit() else 0)
        ttk.Spinbox(row_a, from_=0, to=31, textvariable=var_a, width=8).pack(side=tk.LEFT)

        row_b = tk.Frame(dlg, bg=self.SURF); row_b.pack(padx=24, fill=tk.X, pady=2)
        tk.Label(row_b, text="Variant B:", bg=self.SURF, fg=self.TEXT,
                 font=("Segoe UI", 9), width=12, anchor="w").pack(side=tk.LEFT)
        var_b = tk.IntVar(value=0)
        ttk.Spinbox(row_b, from_=0, to=31, textvariable=var_b, width=8).pack(side=tk.LEFT)

        result = [None]
        def ok():
            a, b = var_a.get(), var_b.get()
            if a == b:
                messagebox.showerror("Same variant", "Variant A and B must be different.", parent=dlg)
                return
            result[0] = (str(a), str(b)); dlg.destroy()
        bf = tk.Frame(dlg, bg=self.SURF); bf.pack(pady=(12, 16))
        ttk.Button(bf, text="Swap", command=ok,          style="Accent.TButton").pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(bf, text="Cancel", command=dlg.destroy, style="Small.TButton").pack(side=tk.LEFT)
        self.wait_window(dlg)
        if result[0] is None: return

        var_str_a, var_str_b = result[0]

        def find_rec(var_str):
            for i, rec in enumerate(self.eq_table.records):
                if (rec.get("dXSB") == src_tid and rec.get("jJpM") == src_eq and
                        rec.get("VhbR") == src_era and rec.get("FccT") == var_str):
                    return i, rec
            return None, None

        idx_a, rec_a = find_rec(var_str_a)
        idx_b, rec_b = find_rec(var_str_b)

        missing = []
        if rec_a is None: missing.append(f"Variant {var_str_a}")
        if rec_b is None: missing.append(f"Variant {var_str_b}")
        if missing:
            messagebox.showerror("Not found",
                f"Could not find: {', '.join(missing)}\n"
                f"({get_equip_name(src_eq)}, {get_era_name(src_era)})")
            return

        # Push undo for both records, mark modified
        self._push_eq_undo_if_needed()
        self._undo_stack.append((idx_a, bytes(rec_a.raw)))
        self._undo_stack.append((idx_b, bytes(rec_b.raw)))
        self._eq_modified.add(idx_a)
        self._eq_modified.add(idx_b)

        # Swap all zone channel values
        for z in range(9):
            for c in range(3):
                abbrev = ZONE_ABBREVS[z][c]
                val_a = rec_a.get(abbrev)
                val_b = rec_b.get(abbrev)
                rec_a.set(abbrev, val_b)
                rec_b.set(abbrev, val_a)

        self._eq_refresh_label(idx_a)
        self._eq_refresh_label(idx_b)
        # If either swapped record is currently displayed, reload it
        if self.eq_current_idx in (idx_a, idx_b):
            self._eq_load_row(self.eq_current_idx)
        self.dirty = True
        self.status.set(
            f"Swapped all zone colors between Variant {var_str_a} ↔ Variant {var_str_b} "
            f"({get_equip_name(src_eq)}, {get_era_name(src_era)}).")

    def duplicate_row(self):
        if self.eq_current_idx is None or not self.eq_table: return
        if self.eq_table.cur_records >= self.eq_table.max_records:
            messagebox.showerror("Table full",
                f"Table is at max capacity ({self.eq_table.max_records} records)."); return
        src = self.eq_table.records[self.eq_current_idx]
        new_rec = EARecord(self.eq_table.fields, bytes(src.raw))
        self.eq_table.records.append(new_rec)
        self.eq_table.cur_records += 1
        self._eq_refresh_list()
        new_lp = self._visible.index(len(self.eq_table.records)-1)
        self.lb.selection_clear(0, tk.END)
        self.lb.selection_set(new_lp); self.lb.see(new_lp)
        self._eq_load_row(len(self.eq_table.records)-1)
        self.dirty = True

    def _eq_delete_rows(self):
        """Delete all currently selected equipment records."""
        if not self.eq_table: return
        sel = self.lb.curselection()
        if not sel:
            messagebox.showinfo("Nothing selected", "Select one or more records to delete.")
            return
        indices = [self._visible[lp] for lp in sel if lp < len(self._visible)]
        if not indices: return
        count = len(indices)
        msg = (f"Delete {count} selected equipment record(s)?\n\n"
               "This cannot be undone and will clear the undo history.")
        if not messagebox.askyesno("Delete Equipment", msg):
            return
        for idx in sorted(indices, reverse=True):
            del self.eq_table.records[idx]
            self.eq_table.cur_records -= 1
        # Indices are now stale — clear undo state entirely
        self._undo_stack.clear()
        self._eq_modified.clear()
        self._eq_snapshot = None
        self.eq_current_idx = None
        self.dirty = True
        self._eq_refresh_list()
        self.status.set(f"Deleted {count} equipment record(s).")

    def _eq_delete_blank(self):
        """Delete all equipment records where Team ID (dXSB) is 0 (blank/placeholder rows)."""
        if not self.eq_table: return
        blank = [i for i, r in enumerate(self.eq_table.records)
                 if r.get("dXSB") in ("0", "")]
        if not blank:
            messagebox.showinfo("No blank records",
                "No blank equipment records found (Team ID = 0).")
            return
        if not messagebox.askyesno("Delete Blank Equipment",
                f"Delete {len(blank)} blank equipment record(s) (Team ID = 0)?\n\n"
                "This cannot be undone and will clear the undo history."):
            return
        for idx in sorted(blank, reverse=True):
            del self.eq_table.records[idx]
            self.eq_table.cur_records -= 1
        self._undo_stack.clear()
        self._eq_modified.clear()
        self._eq_snapshot = None
        self.eq_current_idx = None
        self.dirty = True
        self._eq_refresh_list()
        self.status.set(f"Deleted {len(blank)} blank equipment record(s).")

    def _eq_export(self):
        """Export all Stock Team Equipment records to a JSON file."""
        import json as _json
        if not self.eq_table:
            messagebox.showinfo("No data", "No equipment table is loaded."); return
        path = filedialog.asksaveasfilename(
            title="Export Stock Team Equipment",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
            initialfile="stock_team_equipment.json")
        if not path: return
        field_names = [f.name for f in self.eq_table.fields]
        records = []
        for rec in self.eq_table.records:
            records.append({fn: rec.get(fn) for fn in field_names})
        payload = {
            "table": TARGET_TABLE,
            "source": os.path.basename(self.db.path) if self.db else "",
            "record_count": len(records),
            "fields": field_names,
            "records": records,
        }
        try:
            with open(path, "w", encoding="utf-8") as fh:
                _json.dump(payload, fh, indent=2)
            self.status.set(f"Exported {len(records)} equipment records → {os.path.basename(path)}")
        except Exception as ex:
            messagebox.showerror("Export error", str(ex))

    def _eq_import(self):
        """Import Stock Team Equipment records from a previously exported JSON file."""
        import json as _json
        if not self.eq_table:
            messagebox.showinfo("No data", "No equipment table is loaded."); return
        path = filedialog.askopenfilename(
            title="Import Stock Team Equipment",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not path: return
        try:
            with open(path, "r", encoding="utf-8") as fh:
                payload = _json.load(fh)
        except Exception as ex:
            messagebox.showerror("Import error", str(ex)); return
        if payload.get("table") != TARGET_TABLE:
            messagebox.showerror("Wrong table",
                f"This file is for table '{payload.get('table')}', "
                f"but the loaded DB uses '{TARGET_TABLE}'.")
            return
        incoming = payload.get("records", [])
        if not incoming:
            messagebox.showinfo("Empty file", "No records found in the file."); return

        current = len(self.eq_table.records)
        cap = self.eq_table.max_records
        # Ask: append or replace
        dlg = tk.Toplevel(self); dlg.title("Import Equipment"); dlg.grab_set()
        dlg.configure(bg=self.SURF); dlg.resizable(False, False)
        tk.Label(dlg, text=f"Importing {len(incoming)} records from:",
                 bg=self.SURF, fg=self.TEXT, font=("Segoe UI", 9, "bold")).pack(padx=24, pady=(16, 2))
        tk.Label(dlg, text=os.path.basename(path),
                 bg=self.SURF, fg=self.SUB, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 8))
        tk.Label(dlg, text=f"Current records: {current}   Max capacity: {cap}",
                 bg=self.SURF, fg=self.DIM, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 10))
        mode = tk.StringVar(value="replace")
        rf = tk.Frame(dlg, bg=self.SURF); rf.pack(padx=24, fill=tk.X, pady=2)
        ttk.Radiobutton(rf, text="Replace all existing records with imported ones",
                        variable=mode, value="replace").pack(anchor="w")
        af = tk.Frame(dlg, bg=self.SURF); af.pack(padx=24, fill=tk.X, pady=2)
        ttk.Radiobutton(af, text="Append imported records to existing ones",
                        variable=mode, value="append").pack(anchor="w")
        result = [None]
        def ok(): result[0] = mode.get(); dlg.destroy()
        bf = tk.Frame(dlg, bg=self.SURF); bf.pack(pady=(12, 16))
        ttk.Button(bf, text="Import", command=ok, style="Accent.TButton").pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(bf, text="Cancel", command=dlg.destroy, style="Small.TButton").pack(side=tk.LEFT)
        self.wait_window(dlg)
        if result[0] is None: return

        if result[0] == "replace":
            needed = len(incoming)
        else:
            needed = current + len(incoming)
        if needed > cap:
            messagebox.showerror("Over capacity",
                f"Operation would require {needed} records but table max is {cap}.\n"
                f"{'Remove some records first.' if result[0] == 'append' else 'The import file has too many records.'}")
            return

        if result[0] == "replace":
            self.eq_table.records.clear()
            self.eq_table.cur_records = 0

        added = 0
        for row in incoming:
            new_rec = EARecord(self.eq_table.fields, bytes(self.eq_table.len_bytes))
            for fn, val in row.items():
                new_rec.set(fn, str(val))
            self.eq_table.records.append(new_rec)
            self.eq_table.cur_records += 1
            added += 1

        self._undo_stack.clear()
        self._eq_modified.clear()
        self._eq_snapshot = None
        self.eq_current_idx = None
        self.dirty = True
        self._eq_refresh_list()
        action = "Replaced all with" if result[0] == "replace" else "Appended"
        self.status.set(f"{action} {added} imported equipment records.")

    # ══════════════════════════════════════════════════════════════════════════
    # TEAM SETTINGS TAB
    # ══════════════════════════════════════════════════════════════════════════

    def _build_ts_tab(self, parent):
        pane = ttk.PanedWindow(parent, orient=tk.HORIZONTAL)
        pane.pack(fill=tk.BOTH, expand=True)

        left  = ttk.Frame(pane, width=280); pane.add(left,  weight=1)
        right = ttk.Frame(pane);           pane.add(right, weight=4)

        self._build_ts_list(left)
        self._build_ts_edit(right)

    def _build_ts_list(self, parent):
        sidebar = tk.Frame(parent, bg=self.PANEL)
        sidebar.pack(fill=tk.BOTH, expand=True)

        head = tk.Frame(sidebar, bg=self.PANEL)
        head.pack(fill=tk.X, padx=10, pady=(10, 4))
        tk.Label(head, text="Teams", bg=self.PANEL, fg=self.TEXT,
                 font=("Segoe UI", 11, "bold")).pack(side=tk.LEFT)

        sf = tk.Frame(sidebar, bg=self.LIFT)
        sf.pack(fill=tk.X, padx=10, pady=(0, 6))
        tk.Label(sf, text=" ⌕ ", bg=self.LIFT, fg=self.SUB,
                 font=("Segoe UI", 10)).pack(side=tk.LEFT)
        self.ts_search = tk.StringVar()
        self.ts_search.trace_add("write", lambda *_: self._ts_refresh_list())
        tk.Entry(sf, textvariable=self.ts_search, bg=self.LIFT, fg=self.TEXT,
                 insertbackground=self.TEXT, relief="flat", font=("Segoe UI", 9),
                 bd=0).pack(side=tk.LEFT, fill=tk.X, expand=True, pady=5, padx=(0, 6))

        tk.Frame(sidebar, bg=self.BORDER, height=1).pack(fill=tk.X, padx=10, pady=(0, 4))

        lf = tk.Frame(sidebar, bg=self.PANEL)
        lf.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))
        self.ts_lb = tk.Listbox(lf, bg=self.PANEL, fg=self.TEXT,
                                selectbackground=self.LIFT, selectforeground=self.ACCENT,
                                activestyle="none", relief="flat", bd=0,
                                font=("Consolas", 9), exportselection=False,
                                highlightthickness=0)
        sc = ttk.Scrollbar(lf, command=self.ts_lb.yview)
        self.ts_lb.configure(yscrollcommand=sc.set)
        sc.pack(side=tk.RIGHT, fill=tk.Y)
        self.ts_lb.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.ts_lb.bind("<<ListboxSelect>>", self._ts_on_select)

    def _build_ts_edit(self, parent):
        canvas = tk.Canvas(parent, bg=self.SURF, highlightthickness=0)
        vs = ttk.Scrollbar(parent, orient=tk.VERTICAL, command=canvas.yview)
        canvas.configure(yscrollcommand=vs.set)
        vs.pack(side=tk.RIGHT, fill=tk.Y)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        inner = tk.Frame(canvas, bg=self.SURF)
        win   = canvas.create_window((0, 0), window=inner, anchor="nw")
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(win, width=e.width))
        canvas.bind_all("<MouseWheel>",
                        lambda e: canvas.yview_scroll(-1*(e.delta//120), "units"))

        # ── Identity ────────────────────────────────────────────────────────
        self._section_header(inner, "Identity")
        ig = tk.Frame(inner, bg=self.SURF)
        ig.pack(fill=tk.X, padx=16, pady=(0, 10))
        for idx, abbrev in enumerate(TEAM_IDENTITY_FIELDS):
            col, row = idx % 3, idx // 3
            cell = tk.Frame(ig, bg=self.SURF)
            cell.grid(row=row, column=col, padx=10, pady=4, sticky="w")
            label = TEAM_FIELD_MAP.get(abbrev, abbrev)
            tk.Label(cell, text=label, bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
            tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                     font=("Segoe UI", 7)).pack(anchor=tk.W)
            var = tk.StringVar()
            var.trace_add("write", self._ts_on_change)
            self.ts_vars[abbrev] = var
            w = 22 if abbrev in ("YmkJ", "QNTI") else 14
            ttk.Entry(cell, textvariable=var, width=w).pack(anchor=tk.W, pady=(2, 0))

        # ── League / Organization ────────────────────────────────────────────
        self._section_header(inner, "League / Organization")
        lg = tk.Frame(inner, bg=self.SURF)
        lg.pack(fill=tk.X, padx=16, pady=(0, 10))
        for idx, abbrev in enumerate(TEAM_LEAGUE_FIELDS):
            col, row = idx % 4, idx // 4
            cell = tk.Frame(lg, bg=self.SURF)
            cell.grid(row=row, column=col, padx=10, pady=4, sticky="w")
            label = TEAM_FIELD_MAP.get(abbrev, abbrev)
            tk.Label(cell, text=label, bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
            tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                     font=("Segoe UI", 7)).pack(anchor=tk.W)
            var = tk.StringVar()
            var.trace_add("write", self._ts_on_change)
            self.ts_vars[abbrev] = var
            ttk.Entry(cell, textvariable=var, width=10).pack(anchor=tk.W, pady=(2, 0))

        # ── Rivals ───────────────────────────────────────────────────────────
        self._section_header(inner, "Rivals")
        rv = tk.Frame(inner, bg=self.SURF)
        rv.pack(fill=tk.X, padx=16, pady=(0, 10))
        for idx, abbrev in enumerate(TEAM_RIVAL_FIELDS):
            cell = tk.Frame(rv, bg=self.SURF)
            cell.grid(row=0, column=idx, padx=10, pady=4, sticky="w")
            label = TEAM_FIELD_MAP.get(abbrev, abbrev)
            tk.Label(cell, text=label, bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
            tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                     font=("Segoe UI", 7)).pack(anchor=tk.W)
            var = tk.StringVar()
            var.trace_add("write", self._ts_on_change)
            self.ts_vars[abbrev] = var
            ttk.Entry(cell, textvariable=var, width=12).pack(anchor=tk.W, pady=(2, 0))

        # ── IDs ──────────────────────────────────────────────────────────────
        self._section_header(inner, "IDs")
        ig2 = tk.Frame(inner, bg=self.SURF)
        ig2.pack(fill=tk.X, padx=16, pady=(0, 10))
        for idx, abbrev in enumerate(TEAM_ID_FIELDS):
            col, row = idx % 4, idx // 4
            cell = tk.Frame(ig2, bg=self.SURF)
            cell.grid(row=row, column=col, padx=10, pady=4, sticky="w")
            label = TEAM_FIELD_MAP.get(abbrev, abbrev)
            tk.Label(cell, text=label, bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
            tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                     font=("Segoe UI", 7)).pack(anchor=tk.W)
            var = tk.StringVar()
            var.trace_add("write", self._ts_on_change)
            self.ts_vars[abbrev] = var
            ttk.Entry(cell, textvariable=var, width=10).pack(anchor=tk.W, pady=(2, 0))

        # ── Colors ───────────────────────────────────────────────────────────
        self._section_header(inner, "Team Colors")
        cg = tk.Frame(inner, bg=self.SURF)
        cg.pack(fill=tk.X, padx=16, pady=(0, 6))

        # Column headers
        ch = tk.Frame(cg, bg=self.SURF)
        ch.grid(row=0, column=0, columnspan=6, sticky="w", pady=(0, 4))
        tk.Label(ch, text="Color",   bg=self.SURF, fg=self.SUB,   font=("Segoe UI", 8, "bold"), width=14, anchor="w").pack(side=tk.LEFT)
        tk.Label(ch, text="Red",     bg=self.SURF, fg=self.RED,   font=("Segoe UI", 8, "bold"), width=8).pack(side=tk.LEFT, padx=2)
        tk.Label(ch, text="Green",   bg=self.SURF, fg=self.GREEN, font=("Segoe UI", 8, "bold"), width=8).pack(side=tk.LEFT, padx=2)
        tk.Label(ch, text="Blue",    bg=self.SURF, fg=self.BLUE,  font=("Segoe UI", 8, "bold"), width=8).pack(side=tk.LEFT, padx=2)
        tk.Label(ch, text="Preview", bg=self.SURF, fg=self.SUB,   font=("Segoe UI", 8, "bold"), width=8).pack(side=tk.LEFT, padx=6)

        for ci, (color_label, color_fields, swatch_attr) in enumerate([
            ("1st Color", TEAM_COLOR1, "ts_color1_swatch"),
            ("2nd Color", TEAM_COLOR2, "ts_color2_swatch"),
        ]):
            row_bg = self.PANEL if ci % 2 == 0 else self.SURF
            rf = tk.Frame(cg, bg=row_bg)
            rf.grid(row=ci+1, column=0, columnspan=6, sticky="ew", pady=1)
            tk.Label(rf, text=color_label, bg=row_bg, fg=self.ACCENT,
                     font=("Segoe UI", 9, "bold"), width=14, anchor="w", padx=4).pack(side=tk.LEFT, pady=4)
            border_colors = [self.RED, self.GREEN, self.BLUE]
            for ch_idx, abbrev in enumerate(color_fields):
                var = tk.StringVar()
                var.trace_add("write", self._ts_on_change)
                self.ts_vars[abbrev] = var
                ef = tk.Frame(rf, bg=border_colors[ch_idx], padx=1, pady=1)
                ef.pack(side=tk.LEFT, padx=3, pady=3)
                ttk.Entry(ef, textvariable=var, width=6).pack()
            sw = tk.Label(rf, text="        ", bg=self.LIFT, width=8,
                          cursor="hand2", relief="flat")
            sw.pack(side=tk.LEFT, padx=(8, 2), pady=3)
            sw.bind("<Button-1>", lambda e, ci=ci: self._ts_pick_color(ci))
            setattr(self, swatch_attr, sw)
            tk.Button(rf, text="Pick", bg=self.LIFT, fg=self.SUB,
                      relief="flat", font=("Segoe UI", 8), cursor="hand2",
                      activebackground=self.BORDER, activeforeground=self.TEXT,
                      bd=0, padx=6, pady=2,
                      command=lambda ci=ci: self._ts_pick_color(ci)).pack(side=tk.LEFT, padx=4)
            abbrev_lbl = "  ".join(color_fields)
            tk.Label(rf, text=abbrev_lbl, bg=row_bg, fg=self.DIM,
                     font=("Consolas", 7)).pack(side=tk.LEFT, padx=(4, 0))

        # Undo button
        br = tk.Frame(inner, bg=self.SURF)
        br.pack(fill=tk.X, padx=16, pady=(10, 16))
        ttk.Button(br, text="⟲ Undo  (Ctrl+Shift+Z)", command=self._ts_undo,
                   style="Small.TButton").pack(side=tk.LEFT)

    # ── Team Settings list & logic ─────────────────────────────────────────────

    def _ts_row_label(self, i):
        r = self.ts_table.records[i]
        name  = r.get("YmkJ").strip().rstrip('\x00').strip()
        abbr  = r.get("xsnn").strip().rstrip('\x00').strip()
        idx   = r.get("vfEq")
        dirty = " ×" if i in self._ts_modified else ""
        return f"[{idx}] {abbr}  {name}{dirty}"

    def _ts_refresh_list(self):
        ft = self.ts_search.get().lower()
        self.ts_lb.delete(0, tk.END); self._ts_visible = []
        if not self.ts_table: return
        indices = sorted(range(len(self.ts_table.records)),
                         key=lambda i: (int(self.ts_table.records[i].get("vfEq") or 0)))
        for i in indices:
            label = self._ts_row_label(i)
            if ft and ft not in label.lower(): continue
            self.ts_lb.insert(tk.END, label); self._ts_visible.append(i)

    def _ts_on_select(self, _):
        sel = self.ts_lb.curselection()
        if not sel: return
        lp = sel[0]
        if lp >= len(self._ts_visible): return
        self._ts_load_row(self._ts_visible[lp])

    def _ts_load_row(self, idx):
        self.ts_current_idx = idx
        rec = self.ts_table.records[idx]
        self._ts_snapshot = (idx, bytes(rec.raw))
        self._ts_suppress = True
        for abbrev, var in self.ts_vars.items():
            val = rec.get(abbrev)
            # Strip null bytes from string fields
            if isinstance(val, str):
                val = val.rstrip('\x00').strip()
            var.set(val)
        self._ts_update_swatch(0)
        self._ts_update_swatch(1)
        self._ts_suppress = False

    def _ts_push_undo_if_needed(self):
        if self._ts_snapshot is not None:
            self._ts_undo_stack.append(self._ts_snapshot)
            self._ts_modified.add(self._ts_snapshot[0])
            self._ts_snapshot = None

    def _ts_undo(self):
        if not self._ts_undo_stack:
            self.status.set("Nothing to undo."); return
        idx, raw = self._ts_undo_stack.pop()
        self.ts_table.records[idx].raw[:] = bytearray(raw)
        self._ts_modified.discard(idx)
        self._ts_load_row(idx)
        try:
            lp = self._ts_visible.index(idx)
            self.ts_lb.delete(lp); self.ts_lb.insert(lp, self._ts_row_label(idx))
            self.ts_lb.selection_clear(0, tk.END)
            self.ts_lb.selection_set(lp); self.ts_lb.see(lp)
        except ValueError:
            pass
        self.dirty = True
        self.status.set(f"Undo: team record {idx} restored.")

    def _ts_on_change(self, *_):
        if self._ts_suppress or self.ts_current_idx is None: return
        self._ts_push_undo_if_needed()
        rec = self.ts_table.records[self.ts_current_idx]
        for abbrev, var in self.ts_vars.items():
            rec.set(abbrev, var.get())
        self._ts_update_swatch(0)
        self._ts_update_swatch(1)
        self.dirty = True
        try:
            lp = self._ts_visible.index(self.ts_current_idx)
            self.ts_lb.delete(lp); self.ts_lb.insert(lp, self._ts_row_label(self.ts_current_idx))
            self.ts_lb.selection_set(lp)
        except ValueError:
            pass

    def _ts_update_swatch(self, ci):
        try:
            fields = TEAM_COLOR1 if ci == 0 else TEAM_COLOR2
            r = _channel_byte(self.ts_vars[fields[0]].get())
            g = _channel_byte(self.ts_vars[fields[1]].get())
            b = _channel_byte(self.ts_vars[fields[2]].get())
            color = f"#{r:02X}{g:02X}{b:02X}"
            lum   = 0.299*r + 0.587*g + 0.114*b
            sw = self.ts_color1_swatch if ci == 0 else self.ts_color2_swatch
            if sw: sw.configure(bg=color, fg="#000" if lum > 128 else "#fff")
        except Exception:
            pass

    def _ts_pick_color(self, ci):
        try:
            fields = TEAM_COLOR1 if ci == 0 else TEAM_COLOR2
            r = _channel_byte(self.ts_vars[fields[0]].get())
            g = _channel_byte(self.ts_vars[fields[1]].get())
            b = _channel_byte(self.ts_vars[fields[2]].get())
            init = f"#{r:02X}{g:02X}{b:02X}"
        except Exception:
            init = "#000000"
        label = "1st Color" if ci == 0 else "2nd Color"
        result = colorchooser.askcolor(color=init, title=f"Pick {label}")
        if result and result[0]:
            self._ts_push_undo_if_needed()
            ri, gi, bi = [int(x) for x in result[0]]
            fields = TEAM_COLOR1 if ci == 0 else TEAM_COLOR2
            self._ts_suppress = True
            self.ts_vars[fields[0]].set(str(ri))
            self.ts_vars[fields[1]].set(str(gi))
            self.ts_vars[fields[2]].set(str(bi))
            self._ts_suppress = False
            if self.ts_current_idx is not None:
                rec = self.ts_table.records[self.ts_current_idx]
                for abbrev in fields:
                    rec.set(abbrev, self.ts_vars[abbrev].get())
                self._ts_update_swatch(ci)
                self.dirty = True
                try:
                    lp = self._ts_visible.index(self.ts_current_idx)
                    self.ts_lb.delete(lp)
                    self.ts_lb.insert(lp, self._ts_row_label(self.ts_current_idx))
                    self.ts_lb.selection_set(lp)
                except ValueError:
                    pass

    # ══════════════════════════════════════════════════════════════════════════
    # JERSEY TAB
    # ══════════════════════════════════════════════════════════════════════════

    def _build_jy_tab(self, parent):
        pane = ttk.PanedWindow(parent, orient=tk.HORIZONTAL)
        pane.pack(fill=tk.BOTH, expand=True)

        left = ttk.Frame(pane, width=320); pane.add(left, weight=1)
        self._build_jy_list(left)

        right = ttk.Frame(pane); pane.add(right, weight=4)
        self._build_jy_edit(right)

    def _build_jy_list(self, parent):
        # Sidebar panel
        sidebar = tk.Frame(parent, bg=self.PANEL)
        sidebar.pack(fill=tk.BOTH, expand=True)

        # Header
        head = tk.Frame(sidebar, bg=self.PANEL)
        head.pack(fill=tk.X, padx=10, pady=(10, 4))
        tk.Label(head, text="Jerseys", bg=self.PANEL, fg=self.TEXT,
                 font=("Segoe UI", 11, "bold")).pack(side=tk.LEFT)

        # Search bar
        sf = tk.Frame(sidebar, bg=self.LIFT)
        sf.pack(fill=tk.X, padx=10, pady=(0, 6))
        tk.Label(sf, text=" ⌕ ", bg=self.LIFT, fg=self.SUB,
                 font=("Segoe UI", 10)).pack(side=tk.LEFT)
        self.jy_search = tk.StringVar()
        self.jy_search.trace_add("write", lambda *_: self._jy_refresh_list())
        tk.Entry(sf, textvariable=self.jy_search, bg=self.LIFT, fg=self.TEXT,
                 insertbackground=self.TEXT, relief="flat", font=("Segoe UI", 9),
                 bd=0).pack(side=tk.LEFT, fill=tk.X, expand=True, pady=5, padx=(0, 6))

        # Action buttons — row 1
        bf1 = tk.Frame(sidebar, bg=self.PANEL)
        bf1.pack(fill=tk.X, padx=10, pady=(0, 3))
        ttk.Button(bf1, text="Add Jersey",  command=self._jy_add_row,    style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf1, text="Delete",      command=self._jy_delete_row, style="Danger.TButton").pack(side=tk.LEFT)

        # Action buttons — row 2
        bf2 = tk.Frame(sidebar, bg=self.PANEL)
        bf2.pack(fill=tk.X, padx=10, pady=(0, 3))
        ttk.Button(bf2, text="▲ Move Up",   command=self._jy_move_up,   style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf2, text="▼ Move Down", command=self._jy_move_down, style="Small.TButton").pack(side=tk.LEFT)

        # Action buttons — row 3
        bf3 = tk.Frame(sidebar, bg=self.PANEL)
        bf3.pack(fill=tk.X, padx=10, pady=(0, 6))
        ttk.Button(bf3, text="Export…", command=self._jy_export, style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf3, text="Import…", command=self._jy_import, style="Small.TButton").pack(side=tk.LEFT)

        # Divider
        tk.Frame(sidebar, bg=self.BORDER, height=1).pack(fill=tk.X, padx=10, pady=(0, 4))

        # Listbox
        lf = tk.Frame(sidebar, bg=self.PANEL)
        lf.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))
        self.jy_lb = tk.Listbox(lf, bg=self.PANEL, fg=self.TEXT,
                                selectbackground=self.LIFT, selectforeground=self.ACCENT,
                                activestyle="none", relief="flat", bd=0,
                                font=("Consolas", 9), exportselection=False,
                                highlightthickness=0)
        sc = ttk.Scrollbar(lf, command=self.jy_lb.yview)
        self.jy_lb.configure(yscrollcommand=sc.set)
        sc.pack(side=tk.RIGHT, fill=tk.Y)
        self.jy_lb.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.jy_lb.bind("<<ListboxSelect>>", self._jy_on_select)

    def _build_jy_edit(self, parent):
        canvas = tk.Canvas(parent, bg=self.SURF, highlightthickness=0)
        vs = ttk.Scrollbar(parent, orient=tk.VERTICAL, command=canvas.yview)
        canvas.configure(yscrollcommand=vs.set)
        vs.pack(side=tk.RIGHT, fill=tk.Y)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        inner = tk.Frame(canvas, bg=self.SURF)
        win = canvas.create_window((0,0), window=inner, anchor="nw")
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(win, width=e.width))

        # ── Display bar ──────────────────────────────────────────────────────
        disp = tk.Frame(inner, bg=self.PANEL)
        disp.pack(fill=tk.X, padx=10, pady=(10, 6))
        self.jy_name_var = tk.StringVar(value="—")
        tk.Label(disp, textvariable=self.jy_name_var, bg=self.PANEL, fg=self.GREEN,
                 font=("Segoe UI", 10), anchor="w").pack(fill=tk.X, padx=12, pady=8)

        # ── Jersey Info section ──────────────────────────────────────────────
        self._section_header(inner, "Jersey Info")

        col_fields = [
            ["mCXg","tYnx","cVLR","GDQL"],
            ["aEfN","Ughr","tWcJ","UuZg"],
            ["yFSM","vInP","FccT","ddOX"],
        ]

        fg = tk.Frame(inner, bg=self.SURF)
        fg.pack(fill=tk.X, padx=16, pady=(0, 16))

        for col, fields in enumerate(col_fields):
            cf = tk.Frame(fg, bg=self.SURF)
            cf.grid(row=0, column=col, padx=14, pady=2, sticky="nw")
            for abbrev in fields:
                cell = tk.Frame(cf, bg=self.SURF)
                cell.pack(anchor=tk.W, pady=5)
                label = JERSEY_FIELD_MAP.get(abbrev, abbrev)
                tk.Label(cell, text=label, bg=self.SURF, fg=self.ACCENT,
                         font=("Segoe UI", 8, "bold")).pack(anchor=tk.W)
                tk.Label(cell, text=abbrev, bg=self.SURF, fg=self.DIM,
                         font=("Segoe UI", 7)).pack(anchor=tk.W)
                var = tk.StringVar()
                var.trace_add("write", self._jy_on_change)
                self.jy_vars[abbrev] = var
                ttk.Entry(cell, textvariable=var, width=16).pack(anchor=tk.W, pady=(2, 0))
                hint = JERSEY_FIELD_HINTS.get(abbrev)
                if hint:
                    tk.Label(cell, text=hint, bg=self.SURF, fg=self.DIM,
                             font=("Segoe UI", 7), wraplength=180, justify="left").pack(anchor=tk.W)

    # ── Jersey list ────────────────────────────────────────────────────────────

    def _jy_row_label(self, i):
        r       = self.jy_table.records[i]
        tid     = r.get("mCXg")
        jidx    = r.get("tYnx")
        style   = r.get("cVLR")
        art_id  = r.get("GDQL")
        variant = r.get("FccT")
        light   = r.get("Ughr")
        loc     = r.get("UuZg")
        try: sname = JERSEY_STYLE_NAMES.get(int(style), f"Style {style}")
        except: sname = f"Style {style}"
        try: locname = LOC_STRING_NAMES.get(int(loc), f"Loc {loc}")
        except: locname = f"Loc {loc}"
        jersey_id = f"{style}_{art_id}_{variant}"
        return (f"[{jidx}] {get_jersey_team_name(tid)}  "
                f"ID: {jersey_id}  {sname}  [{locname}]")

    def _jy_refresh_list(self):
        ft = self.jy_search.get().lower()
        self.jy_lb.delete(0, tk.END); self._jy_visible = []
        if not self.jy_table: return
        def sort_key(i):
            r = self.jy_table.records[i]
            try: tid  = int(r.get("mCXg") or 0)
            except: tid = 0
            try: jidx = int(r.get("tYnx") or 0)
            except: jidx = 0
            return (tid, jidx)
        indices = sorted(range(len(self.jy_table.records)), key=sort_key)
        for i in indices:
            label = self._jy_row_label(i)
            if ft and ft not in label.lower(): continue
            self.jy_lb.insert(tk.END, label); self._jy_visible.append(i)

    def _jy_on_select(self, _):
        sel = self.jy_lb.curselection()
        if not sel: return
        lp = sel[0]
        if lp >= len(self._jy_visible): return
        self._jy_load_row(self._jy_visible[lp])

    def _jy_load_row(self, idx):
        self.jy_current_idx = idx
        rec = self.jy_table.records[idx]
        self._jy_suppress = True
        for abbrev, var in self.jy_vars.items():
            var.set(rec.get(abbrev))
        self._jy_suppress = False
        self._jy_update_display()

    def _jy_on_change(self, *_):
        if self._jy_suppress or self.jy_current_idx is None: return
        rec = self.jy_table.records[self.jy_current_idx]
        for abbrev, var in self.jy_vars.items():
            rec.set(abbrev, var.get())
        self.dirty = True
        self._jy_update_display()
        try:
            lp = self._jy_visible.index(self.jy_current_idx)
            self.jy_lb.delete(lp)
            self.jy_lb.insert(lp, self._jy_row_label(self.jy_current_idx))
            self.jy_lb.selection_set(lp)
        except ValueError:
            pass

    def _jy_update_display(self):
        tid    = self.jy_vars.get("mCXg", tk.StringVar()).get()
        style  = self.jy_vars.get("cVLR", tk.StringVar()).get()
        light  = self.jy_vars.get("Ughr", tk.StringVar()).get()
        var    = self.jy_vars.get("FccT", tk.StringVar()).get()
        jidx   = self.jy_vars.get("tYnx", tk.StringVar()).get()
        art_id = self.jy_vars.get("GDQL", tk.StringVar()).get()
        loc    = self.jy_vars.get("UuZg", tk.StringVar()).get()
        try: sname = JERSEY_STYLE_NAMES.get(int(style), f"Style {style}")
        except: sname = f"Style {style}"
        try: lname = LIGHT_COLOR_NAMES.get(int(light), "")
        except: lname = ""
        try: locname = LOC_STRING_NAMES.get(int(loc), f"Loc {loc}")
        except: locname = f"Loc {loc}"
        jersey_id = f"{style}_{art_id}_{var}"
        self.jy_name_var.set(
            f"{get_jersey_team_name(tid)}  |  Jersey ID: {jersey_id}  |  "
            f"Index {jidx}  |  {sname}  |  {lname}  |  {locname}"
        )

    def _jy_add_row(self):
        if not self.jy_table: return
        if self.jy_table.cur_records >= self.jy_table.max_records:
            messagebox.showerror("Table full",
                f"Table is at max capacity ({self.jy_table.max_records} records)."); return

        # Base on currently selected row if available, else first record
        if self.jy_current_idx is not None:
            src = self.jy_table.records[self.jy_current_idx]
        else:
            src = self.jy_table.records[0]

        new_rec = EARecord(self.jy_table.fields, bytes(src.raw))

        # Auto-increment Jersey Index and Variant for this team
        try:
            tid = int(src.get("mCXg") or 0)
            max_idx = max(
                int(r.get("tYnx") or 0)
                for r in self.jy_table.records
                if r.get("mCXg") == str(tid)
            )
            max_var = max(
                int(r.get("FccT") or 0)
                for r in self.jy_table.records
                if r.get("mCXg") == str(tid)
            )
            new_rec.set("tYnx", str(max_idx + 1))
            new_rec.set("FccT", str(max_var + 1))
            new_rec.set("ddOX", str(max_var + 1))
        except Exception:
            pass

        self.jy_table.records.append(new_rec)
        self.jy_table.cur_records += 1
        self._jy_refresh_list()
        new_lp = self._jy_visible.index(len(self.jy_table.records)-1)
        self.jy_lb.selection_clear(0, tk.END)
        self.jy_lb.selection_set(new_lp); self.jy_lb.see(new_lp)
        self._jy_load_row(len(self.jy_table.records)-1)
        self.dirty = True

    def _jy_move_up(self):
        self._jy_move(-1)

    def _jy_move_down(self):
        self._jy_move(1)

    def _jy_move(self, direction):
        """Swap Jersey Index (tYnx) with the adjacent same-team entry in the visible list."""
        if self.jy_current_idx is None or not self.jy_table: return
        try:
            lp = self._jy_visible.index(self.jy_current_idx)
        except ValueError:
            return
        rec_a = self.jy_table.records[self.jy_current_idx]
        tid = rec_a.get("mCXg")

        # Find the nearest neighbor in the same direction that belongs to the same team
        neighbor_lp = None
        step = direction
        check = lp + step
        while 0 <= check < len(self._jy_visible):
            candidate = self.jy_table.records[self._jy_visible[check]]
            if candidate.get("mCXg") == tid:
                neighbor_lp = check
                break
            check += step

        if neighbor_lp is None:
            return  # no same-team neighbor in that direction

        rec_b = self.jy_table.records[self._jy_visible[neighbor_lp]]

        # Swap Jersey Index values
        idx_a = rec_a.get("tYnx")
        idx_b = rec_b.get("tYnx")
        rec_a.set("tYnx", idx_b)
        rec_b.set("tYnx", idx_a)

        self.dirty = True
        self._jy_refresh_list()

        # Re-select the moved row (it may have shifted position after resort)
        try:
            new_lp = self._jy_visible.index(self.jy_current_idx)
            self.jy_lb.selection_clear(0, tk.END)
            self.jy_lb.selection_set(new_lp)
            self.jy_lb.see(new_lp)
        except ValueError:
            pass
        # Update the index field shown in edit panel
        self._jy_suppress = True
        self.jy_vars["tYnx"].set(rec_a.get("tYnx"))
        self._jy_suppress = False
        self._jy_update_display()

    def _jy_delete_row(self):
        if self.jy_current_idx is None or not self.jy_table: return
        rec = self.jy_table.records[self.jy_current_idx]
        tid     = rec.get("mCXg")
        jidx    = rec.get("tYnx")
        style   = rec.get("cVLR")
        art_id  = rec.get("GDQL")
        variant = rec.get("FccT")
        jersey_id = f"{style}_{art_id}_{variant}"
        if not messagebox.askyesno("Delete Jersey Row",
                f"Delete jersey ID {jersey_id} for {get_jersey_team_name(tid)}?\n\n"
                f"This will also renumber the remaining Jersey Indices for this team.\n"
                f"Remember to also remove the matching Variant rows in Stock Team Equipment."):
            return

        # Remove the record
        del self.jy_table.records[self.jy_current_idx]
        self.jy_table.cur_records -= 1

        # Renumber Jersey Indices for this team (sort by current index, reassign 0,1,2...)
        team_recs = sorted(
            [r for r in self.jy_table.records if r.get("mCXg") == tid],
            key=lambda r: int(r.get("tYnx") or 0)
        )
        for new_idx, r in enumerate(team_recs):
            r.set("tYnx", str(new_idx))

        self.jy_current_idx = None
        self.jy_name_var.set("—")
        self._jy_refresh_list()
        self.dirty = True
        self.status.set(f"Deleted jersey {jersey_id}. Remaining indices renumbered.")

    def _jy_export(self):
        """Export all Team Jersey records to a JSON file.

        Records are sorted by (team, jersey-index) so that the JSON order
        matches the visual sort order and the physical DB order after import.
        """
        import json as _json
        if not self.jy_table:
            messagebox.showinfo("No data", "No jersey table is loaded."); return
        path = filedialog.asksaveasfilename(
            title="Export Team Jerseys",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
            initialfile="team_jerseys.json")
        if not path: return
        field_names = [f.name for f in self.jy_table.fields]
        # Sort by (tid, tYnx) so physical order in export = display order
        sorted_recs = sorted(self.jy_table.records, key=lambda r: (
            int(r.get("mCXg") or 0),
            int(r.get("tYnx") or 0),
        ))
        records = []
        for rec in sorted_recs:
            records.append({fn: rec.get(fn) for fn in field_names})
        payload = {
            "table": JERSEY_TABLE,
            "source": os.path.basename(self.db.path) if self.db else "",
            "record_count": len(records),
            "fields": field_names,
            "records": records,
        }
        try:
            with open(path, "w", encoding="utf-8") as fh:
                _json.dump(payload, fh, indent=2)
            self.status.set(f"Exported {len(records)} jersey records → {os.path.basename(path)}")
        except Exception as ex:
            messagebox.showerror("Export error", str(ex))

    def _jy_import(self):
        """Import Team Jersey records from a previously exported JSON file."""
        import json as _json
        if not self.jy_table:
            messagebox.showinfo("No data", "No jersey table is loaded."); return
        path = filedialog.askopenfilename(
            title="Import Team Jerseys",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not path: return
        try:
            with open(path, "r", encoding="utf-8") as fh:
                payload = _json.load(fh)
        except Exception as ex:
            messagebox.showerror("Import error", str(ex)); return
        if payload.get("table") != JERSEY_TABLE:
            messagebox.showerror("Wrong table",
                f"This file is for table '{payload.get('table')}', "
                f"but the loaded DB uses '{JERSEY_TABLE}'.")
            return
        incoming = payload.get("records", [])
        if not incoming:
            messagebox.showinfo("Empty file", "No records found in the file."); return

        current = len(self.jy_table.records)
        cap = self.jy_table.max_records
        dlg = tk.Toplevel(self); dlg.title("Import Jerseys"); dlg.grab_set()
        dlg.configure(bg=self.SURF); dlg.resizable(False, False)
        tk.Label(dlg, text=f"Importing {len(incoming)} records from:",
                 bg=self.SURF, fg=self.TEXT, font=("Segoe UI", 9, "bold")).pack(padx=24, pady=(16, 2))
        tk.Label(dlg, text=os.path.basename(path),
                 bg=self.SURF, fg=self.SUB, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 8))
        tk.Label(dlg, text=f"Current records: {current}   Max capacity: {cap}",
                 bg=self.SURF, fg=self.DIM, font=("Segoe UI", 8)).pack(padx=24, pady=(0, 10))
        mode = tk.StringVar(value="replace")
        rf = tk.Frame(dlg, bg=self.SURF); rf.pack(padx=24, fill=tk.X, pady=2)
        ttk.Radiobutton(rf, text="Replace all existing records with imported ones",
                        variable=mode, value="replace").pack(anchor="w")
        af = tk.Frame(dlg, bg=self.SURF); af.pack(padx=24, fill=tk.X, pady=2)
        ttk.Radiobutton(af, text="Append imported records to existing ones",
                        variable=mode, value="append").pack(anchor="w")
        result = [None]
        def ok(): result[0] = mode.get(); dlg.destroy()
        bf = tk.Frame(dlg, bg=self.SURF); bf.pack(pady=(12, 16))
        ttk.Button(bf, text="Import", command=ok, style="Accent.TButton").pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(bf, text="Cancel", command=dlg.destroy, style="Small.TButton").pack(side=tk.LEFT)
        self.wait_window(dlg)
        if result[0] is None: return

        if result[0] == "replace":
            needed = len(incoming)
        else:
            needed = current + len(incoming)
        if needed > cap:
            messagebox.showerror("Over capacity",
                f"Operation would require {needed} records but table max is {cap}.\n"
                f"{'Remove some records first.' if result[0] == 'append' else 'The import file has too many records.'}")
            return

        if result[0] == "replace":
            self.jy_table.records.clear()
            self.jy_table.cur_records = 0

        # Sort by (tid, tYnx) so physical DB order = display sort order.
        # This is critical: the game reads jerseys in physical record order,
        # so the physical position must match the intended visual sequence.
        try:
            sorted_incoming = sorted(incoming, key=lambda r: (
                int(r.get("mCXg") or 0),
                int(r.get("tYnx") or 0),
            ))
        except Exception:
            sorted_incoming = incoming

        added = 0
        for row in sorted_incoming:
            new_rec = EARecord(self.jy_table.fields, bytes(self.jy_table.len_bytes))
            for fn, val in row.items():
                new_rec.set(fn, str(val))
            self.jy_table.records.append(new_rec)
            self.jy_table.cur_records += 1
            added += 1

        self.jy_current_idx = None
        self.jy_name_var.set("—")
        self.dirty = True
        self._jy_refresh_list()
        action = "Replaced all with" if result[0] == "replace" else "Appended"
        self.status.set(f"{action} {added} imported jersey records.")

    # ══════════════════════════════════════════════════════════════════════════
    # LANGUAGE STRINGS TAB
    # ══════════════════════════════════════════════════════════════════════════

    def _build_lang_tab(self, parent):
        s = ttk.Style()

        # Treeview dark theme
        s.configure("Lang.Treeview",
                    background=self.PANEL, foreground=self.TEXT,
                    fieldbackground=self.PANEL, rowheight=22,
                    font=("Consolas", 9), borderwidth=0)
        s.configure("Lang.Treeview.Heading",
                    background=self.LIFT, foreground=self.ACCENT,
                    font=("Segoe UI", 9, "bold"), relief="flat")
        s.map("Lang.Treeview",
              background=[("selected", self.LIFT)],
              foreground=[("selected", self.ACCENT)])

        # ── Toolbar row ──────────────────────────────────────────────────────
        tb = tk.Frame(parent, bg=self.PANEL)
        tb.pack(fill=tk.X, padx=10, pady=(8, 4))
        ttk.Button(tb, text="Load Meta XML",
                   command=self._lang_load_meta,
                   style="Small.TButton").pack(side=tk.LEFT, padx=(0, 6))
        self._lang_reencrypt_btn = ttk.Button(tb, text="Re-encrypt & Save",
                                               command=self._lang_reencrypt_save,
                                               style="Small.TButton")
        self._lang_reencrypt_btn.pack(side=tk.LEFT, padx=(0, 6))
        self._lang_reencrypt_btn.state(["disabled"])
        tk.Frame(tb, bg=self.BORDER, width=1).pack(side=tk.LEFT, fill=tk.Y, pady=2, padx=4)
        self._lang_meta_label = tk.Label(tb, text="No meta XML loaded",
                                          bg=self.PANEL, fg=self.DIM,
                                          font=("Segoe UI", 8))
        self._lang_meta_label.pack(side=tk.LEFT)

        # ── Search row ───────────────────────────────────────────────────────
        sf = tk.Frame(parent, bg=self.LIFT)
        sf.pack(fill=tk.X, padx=10, pady=(0, 6))
        tk.Label(sf, text=" ⌕ ", bg=self.LIFT, fg=self.SUB,
                 font=("Segoe UI", 10)).pack(side=tk.LEFT)
        self._lang_search = tk.StringVar()
        self._lang_search.trace_add("write", lambda *_: self._lang_refresh_filter())
        tk.Entry(sf, textvariable=self._lang_search,
                 bg=self.LIFT, fg=self.TEXT,
                 insertbackground=self.TEXT, relief="flat",
                 font=("Segoe UI", 9), bd=0).pack(
            side=tk.LEFT, fill=tk.X, expand=True, pady=5, padx=(0, 6))

        # ── Treeview ─────────────────────────────────────────────────────────
        tv_frame = tk.Frame(parent, bg=self.PANEL)
        tv_frame.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 4))

        cols = ("hashid", "stringid", "sourcetext")
        self._lang_tree = ttk.Treeview(tv_frame, columns=cols,
                                        show="headings", selectmode="browse",
                                        style="Lang.Treeview")
        for col in cols:
            self._lang_tree.heading(col, text=col,
                                    command=lambda c=col: self._lang_sort(c))
        self._lang_tree.column("hashid",     width=110, minwidth=80,  stretch=False)
        self._lang_tree.column("stringid",   width=220, minwidth=100, stretch=False)
        self._lang_tree.column("sourcetext", width=600, minwidth=200, stretch=True)

        vsb = ttk.Scrollbar(tv_frame, orient=tk.VERTICAL,
                             command=self._lang_tree.yview)
        hsb = ttk.Scrollbar(tv_frame, orient=tk.HORIZONTAL,
                             command=self._lang_tree.xview)
        self._lang_tree.configure(yscrollcommand=vsb.set,
                                   xscrollcommand=hsb.set)
        hsb.pack(side=tk.BOTTOM, fill=tk.X)
        vsb.pack(side=tk.RIGHT,  fill=tk.Y)
        self._lang_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self._lang_tree.bind("<Double-1>", self._lang_inline_edit)
        self._lang_tree.tag_configure("odd",  background=self.PANEL)
        self._lang_tree.tag_configure("even", background=self.SURF)

        # ── Button bar ───────────────────────────────────────────────────────
        bf = tk.Frame(parent, bg=self.PANEL)
        bf.pack(fill=tk.X, padx=10, pady=(0, 8))
        ttk.Button(bf, text="Add Row",
                   command=self._lang_add_row,
                   style="Small.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf, text="Delete Row",
                   command=self._lang_delete_row,
                   style="Danger.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(bf, text="Edit Selected",
                   command=self._lang_edit_selected,
                   style="Small.TButton").pack(side=tk.LEFT)
        self._lang_count_label = tk.Label(bf, text="",
                                           bg=self.PANEL, fg=self.DIM,
                                           font=("Segoe UI", 8))
        self._lang_count_label.pack(side=tk.RIGHT)

    # ── Lang helpers ────────────────────────────────────────────────────────

    def _lang_field_name(self, shortname):
        """Return long name if meta loaded, else short name."""
        return self._lang_meta_fields.get(shortname, shortname)

    def _lang_col_headers(self):
        """Return (hashid_label, stringid_label, sourcetext_label) using meta if available."""
        t = self.lang_table
        if t is None:
            return ("hashid", "stringid", "sourcetext")
        snames = [f.name for f in t.fields]
        labels = [self._lang_field_name(s) for s in snames]
        # pad/trim to 3
        while len(labels) < 3:
            labels.append("")
        return tuple(labels[:3])

    def _lang_populate(self):
        """Rebuild internal row cache from lang_table records."""
        t = self.lang_table
        if t is None:
            self._lang_all_rows = []
            return
        fields = t.fields
        rows = []
        for i, rec in enumerate(t.records):
            vals = tuple(rec.get(f.name) for f in fields)
            rows.append((i,) + vals)
        self._lang_all_rows = rows

    def _lang_refresh_filter(self):
        """Re-populate treeview based on current search text."""
        if self._lang_tree is None:
            return
        q = (self._lang_search.get() if self._lang_search else "").lower()
        self._lang_tree.delete(*self._lang_tree.get_children())
        count = 0
        for row in self._lang_all_rows:
            rec_idx = row[0]
            vals    = row[1:]
            if q and not any(q in str(v).lower() for v in vals):
                continue
            tag = "odd" if count % 2 else "even"
            self._lang_tree.insert("", tk.END,
                                    iid=str(rec_idx),
                                    values=vals,
                                    tags=(tag,))
            count += 1
        if self._lang_count_label:
            total = len(self._lang_all_rows)
            shown = count
            self._lang_count_label.configure(
                text=f"{shown} / {total} records shown")

    def _lang_sort(self, col):
        """Sort treeview by column (toggle asc/desc)."""
        tree = self._lang_tree
        children = [(tree.set(k, col), k) for k in tree.get_children()]
        try:
            children.sort(key=lambda x: int(x[0]))
        except ValueError:
            children.sort(key=lambda x: x[0].lower())
        for idx, (_, k) in enumerate(children):
            tree.move(k, "", idx)
            tree.item(k, tags=("odd" if idx % 2 else "even",))

    def _lang_apply_meta_from_path(self, path):
        """Apply a meta XML file (by path) to update column headings."""
        _, field_map = load_meta_xml(path)
        if self.lang_table:
            self._lang_meta_fields = field_map.get(self.lang_table.name, {})
        else:
            for fsn_map in field_map.values():
                self._lang_meta_fields.update(fsn_map)
        # Update column headings using column-index order
        if self._lang_tree:
            col_ids = self._lang_tree["columns"]
            if self._lang_bridge_path:
                field_names = self._lang_bridge_fields
            elif self.lang_table:
                field_names = [f.name for f in self.lang_table.fields]
            else:
                field_names = []
            for col_idx, sname in enumerate(field_names[:len(col_ids)]):
                long_name = self._lang_meta_fields.get(sname, sname)
                self._lang_tree.heading(col_ids[col_idx], text=long_name)
        if self._lang_meta_label:
            self._lang_meta_label.configure(
                text=f"Meta: {os.path.basename(path)}", fg=self.GREEN)

    def _lang_load_meta(self):
        path = filedialog.askopenfilename(
            title="Load meta XML",
            filetypes=[("XML files", "*.xml"), ("All files", "*.*")])
        if not path:
            return
        self._lang_apply_meta_from_path(path)

    def _lang_get_selected_idx(self):
        """Return selected record index (int) or None."""
        sel = self._lang_tree.selection() if self._lang_tree else ()
        if not sel:
            messagebox.showinfo("No selection", "Select a row first.")
            return None
        return int(sel[0])

    def _lang_edit_dialog(self, title, initial_vals):
        """Show a modal dialog for editing lang fields.
        initial_vals: list of (label, shortname, current_value, is_large_text)
        Returns dict {shortname: new_value} or None if cancelled.
        """
        dlg = tk.Toplevel(self)
        dlg.title(title)
        dlg.configure(bg=self.SURF)
        dlg.resizable(True, True)
        dlg.transient(self)
        dlg.grab_set()

        result = {}
        vars_map = {}

        for row_idx, (label, sname, cur_val, is_large) in enumerate(initial_vals):
            tk.Label(dlg, text=label, bg=self.SURF, fg=self.ACCENT,
                     font=("Segoe UI", 9, "bold")).grid(
                row=row_idx*2, column=0, sticky="w", padx=14, pady=(10, 0))
            if is_large:
                var = tk.StringVar(value=cur_val)
                txt = tk.Text(dlg, width=70, height=6,
                              bg=self.LIFT, fg=self.TEXT,
                              insertbackground=self.TEXT,
                              font=("Consolas", 9), relief="flat")
                txt.insert("1.0", cur_val)
                txt.grid(row=row_idx*2+1, column=0, padx=14, pady=(2, 0), sticky="ew")
                vars_map[sname] = ("text", txt)
            else:
                var = tk.StringVar(value=cur_val)
                ttk.Entry(dlg, textvariable=var, width=50).grid(
                    row=row_idx*2+1, column=0, padx=14, pady=(2, 0), sticky="ew")
                vars_map[sname] = ("var", var)

        dlg.columnconfigure(0, weight=1)

        def on_ok():
            for sname, (kind, widget) in vars_map.items():
                if kind == "text":
                    result[sname] = widget.get("1.0", tk.END).rstrip("\n")
                else:
                    result[sname] = widget.get()
            dlg.destroy()

        def on_cancel():
            dlg.destroy()

        btn_frame = tk.Frame(dlg, bg=self.SURF)
        btn_frame.grid(row=len(initial_vals)*2, column=0,
                       padx=14, pady=12, sticky="e")
        ttk.Button(btn_frame, text="OK",     command=on_ok,     style="Accent.TButton").pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(btn_frame, text="Cancel", command=on_cancel, style="TButton").pack(side=tk.LEFT)

        dlg.wait_window()
        return result if result else None

    def _lang_build_dialog_fields(self, rec=None):
        """Build initial_vals list for _lang_edit_dialog.
        rec: EARecord (EATable mode) or dict {shortname: value} (bridge mode).
        """
        if self._lang_bridge_path:
            # Bridge mode: field names come from _lang_bridge_fields
            initial_vals = []
            for i, sname in enumerate(self._lang_bridge_fields):
                label = self._lang_field_name(sname)
                cur   = (rec.get(sname, '') if rec else '')
                # Heuristic: field index 1 (sourcetext/ZbYb) uses a tall text box
                long_name = self._lang_meta_fields.get(sname, sname)
                large = (long_name.lower() in ('sourcetext',) or
                         (i == 1 and len(self._lang_bridge_fields) == 3))
                initial_vals.append((label, sname, cur, large))
            return initial_vals
        t = self.lang_table
        if t is None:
            return []
        initial_vals = []
        for f in t.fields:
            label = self._lang_field_name(f.name)
            cur   = rec.get(f.name) if rec else ""
            large = (f.ftype == FTYPE_STRING and f.bit_count > 800)
            initial_vals.append((label, f.name, cur, large))
        return initial_vals

    def _lang_inline_edit(self, event):
        """Overlay an Entry directly on the clicked cell for in-place editing."""
        tree = self._lang_tree
        iid  = tree.identify_row(event.y)
        col  = tree.identify_column(event.x)
        if not iid or not col:
            return

        col_idx  = int(col[1:]) - 1          # '#1'->0, '#2'->1, '#3'->2
        col_ids  = list(tree["columns"])      # ['hashid','stringid','sourcetext']
        if col_idx >= len(col_ids):
            return
        col_id = col_ids[col_idx]

        # hashid is a numeric key — not editable
        if col_id == "hashid":
            return

        bbox = tree.bbox(iid, col)
        if not bbox:
            return
        x, y, w, h = bbox

        cur_value = tree.set(iid, col_id)
        rec_idx   = int(iid)

        var   = tk.StringVar(value=cur_value)
        entry = tk.Entry(tree, textvariable=var,
                         bg=self.LIFT, fg=self.TEXT,
                         insertbackground=self.TEXT,
                         selectbackground=self.ACCENT,
                         relief="flat", font=("Consolas", 9), bd=2)
        entry.place(x=x, y=y, width=w, height=h)
        entry.focus_set()
        entry.select_range(0, tk.END)

        def commit(ev=None):
            new_val = var.get()
            entry.destroy()
            if new_val == cur_value:
                return
            # Map column position to bridge field shortname
            if self._lang_bridge_path and col_idx < len(self._lang_bridge_fields):
                sname = self._lang_bridge_fields[col_idx]
                # Update in-memory row cache
                idx_in_list = next(
                    (i for i, r in enumerate(self._lang_all_rows) if r[0] == rec_idx), None)
                if idx_in_list is not None:
                    row = list(self._lang_all_rows[idx_in_list])
                    row[col_idx + 1] = new_val
                    self._lang_all_rows[idx_in_list] = tuple(row)
                # Queue edit
                self._lang_pending_edits.append(
                    {"index": rec_idx, "field": sname, "value": new_val})
                self._lang_refresh_filter()
                self.dirty = True
            elif self.lang_table:
                # EATable mode — map col_idx to field shortname
                fields = self.lang_table.fields
                if col_idx < len(fields):
                    rec = self.lang_table.records[rec_idx]
                    rec.set(fields[col_idx].name, new_val)
                    self._lang_populate()
                    self._lang_refresh_filter()
                    self.dirty = True

        def cancel(ev=None):
            entry.destroy()

        entry.bind("<Return>",   commit)
        entry.bind("<Tab>",      commit)
        entry.bind("<Escape>",   cancel)
        entry.bind("<FocusOut>", commit)

    def _lang_edit_selected(self):
        rec_idx = self._lang_get_selected_idx()
        if rec_idx is None:
            return

        if self._lang_bridge_path:
            # Bridge mode: find the row in _lang_all_rows by index
            row = next((r for r in self._lang_all_rows if r[0] == rec_idx), None)
            if row is None:
                return
            cur_vals = dict(zip(self._lang_bridge_fields, row[1:]))
            initial  = self._lang_build_dialog_fields(cur_vals)
            result   = self._lang_edit_dialog("Edit Record", initial)
            if result is None:
                return
            # Update the in-memory row cache
            new_vals = tuple(result.get(f, cur_vals.get(f, ''))
                             for f in self._lang_bridge_fields)
            idx_in_list = next(i for i, r in enumerate(self._lang_all_rows) if r[0] == rec_idx)
            self._lang_all_rows[idx_in_list] = (rec_idx,) + new_vals
            # Queue edits for string fields only (skip integer hashid)
            for sname, val in result.items():
                if val != cur_vals.get(sname, ''):
                    self._lang_pending_edits.append(
                        {"index": rec_idx, "field": sname, "value": val})
            self._lang_refresh_filter()
            self.dirty = bool(self._lang_pending_edits)
            return

        t   = self.lang_table
        rec = t.records[rec_idx]
        initial = self._lang_build_dialog_fields(rec)
        result  = self._lang_edit_dialog("Edit Record", initial)
        if result is None:
            return
        for sname, val in result.items():
            rec.set(sname, val)
        self._lang_populate()
        self._lang_refresh_filter()
        self.dirty = True

    def _lang_add_row(self):
        if self._lang_bridge_path:
            messagebox.showinfo("Not supported",
                "Adding rows to Huffman-compressed lang DBs is not supported\n"
                "(the table is already at max capacity).")
            return
        t = self.lang_table
        if t is None:
            return
        if t.cur_records >= t.max_records:
            messagebox.showerror(
                "Table full",
                f"Cannot add row: table is at max capacity ({t.max_records} records).")
            return
        initial = self._lang_build_dialog_fields(rec=None)
        result  = self._lang_edit_dialog("Add New Row", initial)
        if result is None:
            return
        new_rec = EARecord(t.fields, bytearray(t.len_bytes))
        for sname, val in result.items():
            new_rec.set(sname, val)
        t.records.append(new_rec)
        t.cur_records = len(t.records)
        self._lang_populate()
        self._lang_refresh_filter()
        self.dirty = True
        # Select the newly added row
        new_iid = str(len(t.records) - 1)
        if self._lang_tree.exists(new_iid):
            self._lang_tree.selection_set(new_iid)
            self._lang_tree.see(new_iid)

    def _lang_delete_row(self):
        if self._lang_bridge_path:
            messagebox.showinfo("Not supported",
                "Deleting rows from Huffman-compressed lang DBs is not supported.")
            return
        rec_idx = self._lang_get_selected_idx()
        if rec_idx is None:
            return
        t   = self.lang_table
        rec = t.records[rec_idx]
        # Show a preview of what will be deleted
        preview_field = t.fields[0].name if t.fields else ""
        preview_val   = rec.get(preview_field) if preview_field else ""
        if not messagebox.askyesno(
                "Delete row",
                f"Delete record at index {rec_idx}?\n"
                f"  {self._lang_field_name(preview_field)}: {preview_val}"):
            return
        del t.records[rec_idx]
        t.cur_records = len(t.records)
        self._lang_populate()
        self._lang_refresh_filter()
        self.dirty = True

    def _lang_load(self, db):
        """Load lang DB into the lang tab."""
        self.lang_table = db.get_table(LANG_TABLE)
        if self.lang_table is None:
            messagebox.showerror("Table not found",
                f"Expected table '{LANG_TABLE}' not found in DB.")
            return False
        # Reset meta
        self._lang_meta_fields = {}
        if self._lang_meta_label:
            self._lang_meta_label.configure(text="No meta XML loaded",
                                             fg=self.DIM)
        # Auto-detect sibling meta XML
        import os as _os
        db_dir  = _os.path.dirname(db.path)
        db_base = _os.path.splitext(_os.path.basename(db.path))[0]
        auto_meta = _os.path.join(db_dir, db_base + "-meta.xml")
        if _os.path.exists(auto_meta):
            _, field_map = load_meta_xml(auto_meta)
            self._lang_meta_fields = field_map.get(LANG_TABLE, {})
            if self._lang_meta_label:
                self._lang_meta_label.configure(
                    text=f"Meta: {_os.path.basename(auto_meta)} (auto)", fg=self.GREEN)
        # Update column headings using column-index order
        if self._lang_tree and self.lang_table:
            col_ids = self._lang_tree["columns"]
            for col_idx, f in enumerate(self.lang_table.fields[:len(col_ids)]):
                long_name = self._lang_meta_fields.get(f.name, f.name)
                self._lang_tree.heading(col_ids[col_idx], text=long_name)
        self._lang_populate()
        self._lang_refresh_filter()
        return True

    # ── Bridge (little-endian / Huffman lang DB) ───────────────────────────

    def _lang_find_bridge(self, db_path):
        """Locate nhl_db_bridge.js — look next to the DB, then next to this script."""
        for candidate in [
            os.path.join(os.path.dirname(db_path),    'nhl_db_bridge.js'),
            os.path.join(os.path.dirname(__file__),   'nhl_db_bridge.js'),
            os.path.join(os.path.dirname(os.path.abspath(__file__)), 'nhl_db_bridge.js'),
        ]:
            if os.path.exists(candidate):
                return candidate
        return None

    def _lang_load_from_bridge(self, path):
        """Call Node bridge `list` command; populate _lang_all_rows."""
        import subprocess, json
        bridge = self._lang_find_bridge(path)
        if bridge is None:
            messagebox.showerror("Bridge not found",
                "Could not find nhl_db_bridge.js.\n"
                "Place it in the same folder as the .db file.")
            return False
        cmd = json.dumps({"command": "list", "db_path": path})
        try:
            res = subprocess.run(['node', bridge, cmd],
                                 capture_output=True, text=True, encoding='utf-8',
                                 timeout=60, cwd=os.path.dirname(bridge))
        except FileNotFoundError:
            messagebox.showerror("Node.js not found",
                "node.exe not found — install Node.js and ensure it is on PATH.")
            return False
        except Exception as ex:
            messagebox.showerror("Bridge error", str(ex)); return False
        try:
            data = json.loads(res.stdout)
        except Exception:
            messagebox.showerror("Bridge error",
                f"Invalid JSON from bridge.\nstderr: {res.stderr[:500]}")
            return False
        if not data.get('ok'):
            messagebox.showerror("Bridge error", data.get('error', 'unknown'))
            return False

        self._lang_bridge_path   = path
        self._lang_bridge_fields = [d['name'] for d in data['fieldDefs']]
        self._lang_pending_edits = []

        # Build _lang_all_rows: [(idx, f0_val, f1_val, ...)]
        rows = []
        for rec in data['records']:
            vals = tuple(str(rec['fields'].get(f, '')) for f in self._lang_bridge_fields)
            rows.append((rec['index'],) + vals)
        self._lang_all_rows = rows

        # Auto-load sibling meta XML
        self._lang_meta_fields = {}
        db_dir  = os.path.dirname(path)
        db_base = os.path.splitext(os.path.basename(path))[0]
        auto_meta = os.path.join(db_dir, db_base + '-meta.xml')
        if os.path.exists(auto_meta):
            _, fmap = load_meta_xml(auto_meta)
            for v in fmap.values():
                self._lang_meta_fields.update(v)
            if self._lang_meta_label:
                self._lang_meta_label.configure(
                    text=f"Meta: {os.path.basename(auto_meta)} (auto)", fg=self.GREEN)
        else:
            if self._lang_meta_label:
                self._lang_meta_label.configure(text="No meta XML loaded", fg=self.DIM)

        # Update treeview column headings
        if self._lang_tree:
            col_ids = self._lang_tree["columns"]
            for i, sname in enumerate(self._lang_bridge_fields[:len(col_ids)]):
                long_name = self._lang_meta_fields.get(sname, sname)
                self._lang_tree.heading(col_ids[i], text=long_name)

        self._lang_refresh_filter()
        return True

    def _lang_convert_via_bridge(self, src, dest, direction='xbox-to-pc'):
        """Call Node bridge `convert` command to convert between Xbox (BE) and PC (LE) formats."""
        import subprocess, json
        bridge = self._lang_find_bridge(src)
        if bridge is None:
            messagebox.showerror("Bridge not found", "nhl_db_bridge.js not found.")
            return False
        cmd = json.dumps({
            "command":   "convert",
            "db_path":   src,
            "dest_path": dest,
            "direction": direction,
        })
        try:
            res = subprocess.run(['node', bridge, cmd],
                                 capture_output=True, text=True, encoding='utf-8',
                                 timeout=60, cwd=os.path.dirname(bridge))
        except Exception as ex:
            messagebox.showerror("Convert error", str(ex)); return False
        try:
            data = json.loads(res.stdout)
        except Exception:
            messagebox.showerror("Convert error",
                f"Invalid JSON from bridge.\nstderr: {res.stderr[:500]}")
            return False
        if not data.get('ok'):
            messagebox.showerror("Convert error", data.get('error', 'unknown'))
            return False
        return True

    def _lang_open_bridge_xbox(self, path):
        """Detect Xbox (BE) lang DB, convert to PC temp file, load via bridge."""
        import tempfile
        # Clean up old temp file
        if self._lang_temp_path and os.path.exists(self._lang_temp_path):
            try: os.remove(self._lang_temp_path)
            except: pass
            self._lang_temp_path = None
        # Convert Xbox→PC to a temp file
        fd, tmp = tempfile.mkstemp(suffix='.db', prefix='lang_pc_')
        os.close(fd)
        ok = self._lang_convert_via_bridge(path, tmp, direction='xbox-to-pc')
        if not ok:
            try: os.remove(tmp)
            except: pass
            return
        # Set Xbox state before loading
        self._lang_is_xbox       = True
        self._lang_xbox_src_path = path
        self._lang_temp_path     = tmp
        # Load PC version via bridge (sets bridge path, populates rows, sets tab states)
        self._lang_open_bridge(tmp)
        # Auto-load meta XML from parent directory
        base = os.path.splitext(os.path.basename(path))[0]
        xml_path = os.path.join(os.path.dirname(path), base + '-meta.xml')
        if os.path.exists(xml_path):
            self._lang_apply_meta_from_path(xml_path)
            # Xbox meta XML has reversed 4-char shortnames (e.g. "VhAs" vs PC "sAhV").
            # Remap keys so they match the PC-format names the bridge returns.
            self._lang_meta_fields = {k[::-1]: v for k, v in self._lang_meta_fields.items()}
            # Re-apply column headings with the corrected keys.
            if self._lang_tree:
                col_ids = self._lang_tree["columns"]
                for col_idx, sname in enumerate(self._lang_bridge_fields[:len(col_ids)]):
                    long_name = self._lang_meta_fields.get(sname, sname)
                    self._lang_tree.heading(col_ids[col_idx], text=long_name)
        # Update status with [Xbox] prefix and enable re-encrypt button
        n = len(self._lang_all_rows)
        self.status.set(f"[Xbox] {os.path.basename(path)}  |  Language Strings: {n} records")
        self._lang_reencrypt_btn.state(["!disabled"])

    def _lang_reencrypt_save(self):
        """Save current edits to PC temp file, then re-encrypt back to Xbox format."""
        if not self._lang_is_xbox or not self._lang_xbox_src_path or not self._lang_temp_path:
            messagebox.showinfo("Not an Xbox DB", "No Xbox lang DB is currently loaded.")
            return
        # First flush any pending edits to the PC temp file
        if self._lang_pending_edits:
            ok = self._lang_save_via_bridge(dest_path=self._lang_temp_path)
            if not ok:
                return
        # Ask where to save the re-encrypted Xbox file
        initial = os.path.basename(self._lang_xbox_src_path)
        dest = filedialog.asksaveasfilename(
            title="Save Re-encrypted Xbox DB",
            defaultextension=".db",
            filetypes=[("DB files", "*.db"), ("All files", "*.*")],
            initialfile=initial)
        if not dest:
            return
        ok = self._lang_convert_via_bridge(self._lang_temp_path, dest, direction='pc-to-xbox')
        if ok:
            self.dirty = False
            self.status.set(f"[Xbox] Re-encrypted → {os.path.basename(dest)}")

    def _lang_open_bridge(self, path):
        """Full open flow for little-endian lang DBs (bypasses EADatabase)."""
        ok = self._lang_load_from_bridge(path)
        if not ok:
            return
        self.db         = None
        self.eq_table   = None
        self.jy_table   = None
        self.ts_table   = None
        self.lang_table = None
        self.dirty      = False
        self._eq_modified.clear()
        self._undo_stack.clear()
        self._eq_snapshot = None
        self._ts_modified.clear()
        self._ts_undo_stack.clear()
        self._ts_snapshot = None
        self.ts_current_idx = None
        self._set_tab_states("lang")
        n = len(self._lang_all_rows)
        self.status.set(f"{os.path.basename(path)}  |  Language Strings: {n} records")

    def _lang_save_via_bridge(self, dest_path=None):
        """Call Node bridge `apply-edits` command to save pending edits."""
        import subprocess, json
        if not self._lang_bridge_path:
            return False
        if not self._lang_pending_edits:
            self.status.set("Nothing to save (no edits pending).")
            return True
        bridge = self._lang_find_bridge(self._lang_bridge_path)
        if bridge is None:
            messagebox.showerror("Bridge not found", "nhl_db_bridge.js not found.")
            return False
        target = dest_path or self._lang_bridge_path
        cmd = json.dumps({
            "command":   "apply-edits",
            "db_path":   self._lang_bridge_path,
            "dest_path": target,
            "edits":     self._lang_pending_edits,
        })
        try:
            res = subprocess.run(['node', bridge, cmd],
                                 capture_output=True, text=True, encoding='utf-8',
                                 timeout=60, cwd=os.path.dirname(bridge))
        except Exception as ex:
            messagebox.showerror("Bridge save error", str(ex)); return False
        try:
            data = json.loads(res.stdout)
        except Exception:
            messagebox.showerror("Bridge save error",
                f"Invalid JSON from bridge.\nstderr: {res.stderr[:500]}")
            return False
        if not data.get('ok'):
            messagebox.showerror("Bridge save error", data.get('error', 'unknown'))
            return False
        # On save, update the bridge path if we saved to a new location
        if dest_path:
            self._lang_bridge_path = dest_path
        self._lang_pending_edits = []
        return True

    # ══════════════════════════════════════════════════════════════════════════
    # DB open / save
    # ══════════════════════════════════════════════════════════════════════════

    def _set_tab_states(self, db_type):
        """Enable tabs relevant to db_type, disable the others."""
        nb = self.nb
        if db_type == "equipment":
            nb.tab(self._eq_tab_idx,   state="normal")
            nb.tab(self._jy_tab_idx,   state="normal")
            nb.tab(self._ts_tab_idx,   state="normal")
            nb.tab(self._lang_tab_idx, state="disabled")
            nb.select(self._eq_tab_idx)
        elif db_type == "lang":
            nb.tab(self._eq_tab_idx,   state="disabled")
            nb.tab(self._jy_tab_idx,   state="disabled")
            nb.tab(self._ts_tab_idx,   state="disabled")
            nb.tab(self._lang_tab_idx, state="normal")
            nb.select(self._lang_tab_idx)
        else:
            nb.tab(self._eq_tab_idx,   state="normal")
            nb.tab(self._jy_tab_idx,   state="normal")
            nb.tab(self._ts_tab_idx,   state="normal")
            nb.tab(self._lang_tab_idx, state="normal")

    def open_db(self):
        path = filedialog.askopenfilename(
            title="Open EA .DB file",
            filetypes=[("DB files","*.db *.DB"),("All files","*.*")])
        if not path: return

        # Peek at byte 4: 0 = little-endian (lang DB), 1 = big-endian (equipment DB)
        try:
            with open(path, 'rb') as f:
                hdr = f.read(8)
        except Exception as ex:
            messagebox.showerror("Load error", str(ex)); return

        if len(hdr) >= 5 and hdr[4] == 0:
            # Little-endian (PC format) — route entirely through the Node.js bridge
            self._lang_is_xbox = False
            self._lang_xbox_src_path = None
            self._lang_temp_path = None
            self._lang_reencrypt_btn.state(["disabled"])
            self._lang_open_bridge(path)
            return

        try:
            db = EADatabase(); db.load(path)
        except Exception as ex:
            messagebox.showerror("Load error", str(ex)); return

        db_type = detect_db_type(db)

        if db_type == "lang":
            # Big-endian lang DB = Xbox format — convert to PC then open via bridge
            self._lang_is_xbox = False  # reset; _lang_open_bridge_xbox will set True
            self._lang_reencrypt_btn.state(["disabled"])
            self._lang_open_bridge_xbox(path)
            return

        if db_type == "equipment":
            eq_tbl = db.get_table(TARGET_TABLE)
            jy_tbl = db.get_table(JERSEY_TABLE)
            ts_tbl = db.get_table(TEAM_TABLE)
            missing = []
            if eq_tbl is None: missing.append(TARGET_TABLE)
            if jy_tbl is None: missing.append(JERSEY_TABLE)
            if missing:
                present = ', '.join(t.name for t in db.tables)
                messagebox.showerror("Table not found",
                    f"Missing tables: {', '.join(missing)}\nTables present: {present}")
                return
            self.db = db
            self.eq_table = eq_tbl
            self.jy_table = jy_tbl
            self.ts_table = ts_tbl
            self.lang_table = None
            self._lang_bridge_path = None
            self._lang_bridge_fields = []
            self._lang_pending_edits = []
            self._lang_is_xbox = False
            self._lang_xbox_src_path = None
            self._lang_reencrypt_btn.state(["disabled"])
            self.dirty = False
            self._eq_modified.clear()
            self._undo_stack.clear()
            self._eq_snapshot = None
            self._ts_modified.clear()
            self._ts_undo_stack.clear()
            self._ts_snapshot = None
            self.ts_current_idx = None
            self._set_tab_states("equipment")
            self._eq_refresh_list()
            self._jy_refresh_list()
            self._ts_refresh_list()
            ts_info = f"  |  Teams: {ts_tbl.cur_records} records" if ts_tbl else ""
            self.status.set(
                f"{os.path.basename(path)}  |  "
                f"Equipment: {eq_tbl.cur_records} records  |  "
                f"Jerseys: {jy_tbl.cur_records} records{ts_info}"
            )
            if eq_tbl.records:
                self.lb.selection_set(0); self._eq_on_select(None)
            if jy_tbl.records:
                self.jy_lb.selection_set(0); self._jy_on_select(None)
            if ts_tbl and ts_tbl.records:
                self.ts_lb.selection_set(0); self._ts_on_select(None)

        else:
            present = ', '.join(t.name for t in db.tables)
            messagebox.showerror(
                "Unknown DB type",
                f"Could not identify DB type.\nTables found: {present}")
            return

    def _clear_modified(self):
        self._eq_modified.clear()
        self._eq_refresh_list()

    def save_db(self):
        if self._lang_bridge_path:
            ok = self._lang_save_via_bridge()
            if ok:
                self.dirty = False
                self.status.set(f"Saved → {os.path.basename(self._lang_bridge_path)}")
            return
        if not self.db: return
        try:
            self.db.save(); self.dirty = False
            self._clear_modified()
            self.status.set(f"Saved → {os.path.basename(self.db.path)}")
        except Exception as ex:
            messagebox.showerror("Save error", str(ex))

    def save_db_as(self):
        if self._lang_bridge_path:
            path = filedialog.asksaveasfilename(
                title="Save DB as…", defaultextension=".db",
                filetypes=[("DB files","*.db"),("All files","*.*")],
                initialfile=os.path.basename(self._lang_bridge_path))
            if not path: return
            ok = self._lang_save_via_bridge(dest_path=path)
            if ok:
                self.dirty = False
                self.status.set(f"Saved → {os.path.basename(path)}")
            return
        if not self.db: return
        path = filedialog.asksaveasfilename(
            title="Save DB as…", defaultextension=".db",
            filetypes=[("DB files","*.db"),("All files","*.*")],
            initialfile=os.path.basename(self.db.path))
        if not path: return
        try:
            self.db.save(path); self.dirty = False
            self._clear_modified()
            self.status.set(f"Saved → {os.path.basename(path)}")
        except Exception as ex:
            messagebox.showerror("Save error", str(ex))

    def backup_save(self):
        if self._lang_bridge_path:
            bak = self._lang_bridge_path + ".bak"
            if not os.path.exists(bak):
                shutil.copy2(self._lang_bridge_path, bak)
                self.status.set(f"Backup created: {os.path.basename(bak)}")
            self.save_db()
            return
        if not self.db: return
        bak = self.db.path + ".bak"
        if not os.path.exists(bak):
            shutil.copy2(self.db.path, bak)
            self.status.set(f"Backup created: {os.path.basename(bak)}")
        self.save_db()

    def destroy(self):
        if self.dirty and not messagebox.askyesno("Unsaved changes","Exit without saving?"):
            return
        if self._lang_temp_path and os.path.exists(self._lang_temp_path):
            try: os.remove(self._lang_temp_path)
            except: pass
        super().destroy()


if __name__ == "__main__":
    App().mainloop()
