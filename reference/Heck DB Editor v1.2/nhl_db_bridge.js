/**
 * nhl_db_bridge.js
 * Node.js bridge for reading/writing EA T3DB lang databases (little-endian,
 * Huffman-encoded string pool, dataAllocationType 66).
 *
 * Called as a subprocess from stock_team_equipment_editor.py.
 * Usage:  node nhl_db_bridge.js '<json_args>'
 *
 * Commands:
 *   list        – load DB, return all records as JSON
 *   apply-edits – load DB, apply [{index,field,value}] edits, save
 *
 * Output: single JSON object on stdout.
 */

'use strict';

const fs   = require('fs');
const path = require('path');
const { BitView } = require('bit-buffer');
const huffmanTreeParser = require('./madden-file-tools-master/services/huffmanTreeParser');
const CRC   = require('./madden-file-tools-master/services/CRC');

// ─── constants ────────────────────────────────────────────────────────────────
const FTYPE_STRING  = 0;
const FTYPE_BINARY  = 1;
const FTYPE_SINT    = 2;
const FTYPE_UINT    = 3;
const FTYPE_FLOAT   = 4;
const FTYPE_VARCHAR1 = 13;   // 1-byte pool-offset length prefix
const FTYPE_VARCHAR2 = 14;   // 2-byte pool-offset length prefix

const HEADER_SIZE      = 0x18;   // 24 bytes
const TABLE_DEF_ENTRY  = 8;
const TABLE_HEADER_SZ  = 0x28;   // 40 bytes
const FIELD_DEF_SZ     = 0x10;   // 16 bytes

// ─── response helper ─────────────────────────────────────────────────────────
function respond(obj) {
    process.stdout.write(JSON.stringify(obj));
}

// ─── low-level binary helpers ─────────────────────────────────────────────────
function readUInt32LE(buf, off) { return buf.readUInt32LE(off); }
function readUInt16LE(buf, off) { return buf.readUInt16LE(off); }
function readInt32LE(buf, off)  { return buf.readInt32LE(off);  }

function writeUInt32LE(buf, val, off) { buf.writeUInt32LE(val >>> 0, off); }

/** Read a 4-char table/field shortname (stored in natural order for LE). */
function readName4(buf, off) {
    return String.fromCharCode(buf[off], buf[off+1], buf[off+2], buf[off+3]);
}

// ─── parse ────────────────────────────────────────────────────────────────────
function parseDB(data) {
    // File header (24 bytes)
    if (data[0] !== 0x44 || data[1] !== 0x42) {
        throw new Error('Not a valid EA DB file (missing DB magic)');
    }
    const endian       = data[4];                       // 0 = LE, 1 = BE
    const dbSize       = readUInt32LE(data, 8);
    const tableCount   = readUInt32LE(data, 16);

    // Table-index entries
    let pos = HEADER_SIZE;
    const defs = [];
    for (let i = 0; i < tableCount; i++) {
        defs.push({
            name:   readName4(data, pos),
            offset: readUInt32LE(data, pos + 4),
        });
        pos += TABLE_DEF_ENTRY;
    }
    // Sort by offset so we know adjacent table boundaries
    defs.sort((a, b) => a.offset - b.offset);

    const dataStart = pos;  // where table bodies begin

    // Parse each table
    const tables = [];
    for (const def of defs) {
        const hp = dataStart + def.offset;

        const dataAllocationType = readUInt32LE(data, hp + 4);
        const lengthBytes        = readUInt32LE(data, hp + 8);
        const maxRecords         = readUInt16LE(data, hp + 20);
        const currentRecords     = readUInt16LE(data, hp + 22);
        const numFields          = data[hp + 28];
        const indexCount         = data[hp + 29];

        const fieldStart = hp + TABLE_HEADER_SZ;
        const dataBodyStart = fieldStart + numFields * FIELD_DEF_SZ;

        // Field definitions
        const fieldDefs = [];
        for (let i = 0; i < numFields; i++) {
            const fp = fieldStart + i * FIELD_DEF_SZ;
            fieldDefs.push({
                type:   readUInt32LE(data, fp),
                offset: readUInt32LE(data, fp + 4),   // bit offset in record
                name:   readName4(data, fp + 8),
                bits:   readUInt32LE(data, fp + 12),
            });
        }

        // Determine size of the data body for this table
        let bodySize;
        if (dataAllocationType === 34 || dataAllocationType === 66) {
            // Extends to next table or dbSize-4
            const tableIdx = defs.indexOf(def);
            if (tableIdx < defs.length - 1) {
                bodySize = defs[tableIdx + 1].offset - def.offset - TABLE_HEADER_SZ - numFields * FIELD_DEF_SZ;
            } else {
                bodySize = (dbSize - 4) - dataBodyStart;
            }
        } else {
            bodySize = lengthBytes * currentRecords;
        }

        const dataBody = data.slice(dataBodyStart, dataBodyStart + bodySize);

        // Extra data (string pool) for type 66
        const recordsRawSize  = lengthBytes * maxRecords;
        const extraDataOffset = recordsRawSize;   // within dataBody
        const extraData       = (dataAllocationType === 34 || dataAllocationType === 66)
            ? dataBody.slice(extraDataOffset)
            : null;

        tables.push({
            name: def.name,
            defOffset: def.offset,
            hp,
            dataBodyStart,
            dataAllocationType,
            lengthBytes,
            maxRecords,
            currentRecords,
            numFields,
            indexCount,
            fieldDefs,
            dataBody,
            extraData,
            extraDataOffset,
        });
    }

    return { data, dbSize, dataStart, tableCount, defs, tables };
}

// ─── read one field value from raw record bytes ───────────────────────────────
function readField(raw, def, extraData, huffRoot) {
    const byteOff = def.offset >> 3;

    if (def.type === FTYPE_VARCHAR1 || def.type === FTYPE_VARCHAR2) {
        if (!extraData || !huffRoot) return '';
        const poolOffset   = readUInt32LE(raw, byteOff);
        const prefixBytes  = def.type === FTYPE_VARCHAR1 ? 1 : 2;
        if (poolOffset + prefixBytes > extraData.length) return '';

        // Prefix stores the CHARACTER count (string length), not bit count.
        const charCount = def.type === FTYPE_VARCHAR1
            ? extraData.readUInt8(poolOffset)
            : extraData.readUInt16BE(poolOffset);   // stored big-endian

        // decodeBufferFromRoot iterates the entire buffer, so only pass the
        // bytes needed — each char takes at most ~16 bits, so charCount*2+16
        // bytes is a safe upper bound.
        const dataStart = poolOffset + prefixBytes;
        const dataEnd   = Math.min(dataStart + charCount * 2 + 16, extraData.length);

        try {
            return huffmanTreeParser.decodeBufferFromRoot(
                huffRoot,
                extraData.slice(dataStart, dataEnd),
                charCount   // valueLength = character count for the final .slice()
            );
        } catch (_) { return ''; }
    }

    if (def.type === FTYPE_STRING) {
        const byteLen = def.bits >> 3;
        return raw.slice(byteOff, byteOff + byteLen)
                  .toString('utf8').replace(/\0/g, '');
    }

    if (def.type === FTYPE_SINT) {
        const bv = new BitView(raw, raw.byteOffset);
        const raw32 = bv.getBits(def.offset, def.bits, false);
        // sign-extend
        const sign = (raw32 >> (def.bits - 1)) & 1;
        return sign ? raw32 - (1 << def.bits) : raw32;
    }

    // UINT / FLOAT / BINARY
    const bv = new BitView(raw, raw.byteOffset);
    return bv.getBits(def.offset, def.bits, false);
}

// ─── find Huffman tree boundary ───────────────────────────────────────────────
/**
 * The pool layout is:  [huffman_tree_bytes] [stringid_rec0] [sourcetext_rec0] [stringid_rec1] …
 * The first VARCHAR field of the first record gives us its pool offset, which is
 * exactly the boundary between the Huffman tree and the first string data.
 */
function findHuffmanEnd(table) {
    const raw0 = table.dataBody.slice(0, table.lengthBytes);
    for (const def of table.fieldDefs) {
        if (def.type === FTYPE_VARCHAR1 || def.type === FTYPE_VARCHAR2) {
            return readUInt32LE(raw0, def.offset >> 3);
        }
    }
    return 0;
}

// ─── encode one string using the existing Huffman tree ───────────────────────
function encodeString(huffRoot, str, prefixBytes) {
    const encoded = huffmanTreeParser.encodeStringFromRoot(huffRoot, str);
    const prefix  = Buffer.alloc(prefixBytes);
    // Prefix stores the CHARACTER count (string length), matching TDBHuffmanField behavior.
    const charCount = str.length;
    if (prefixBytes === 1) prefix.writeUInt8(charCount & 0xFF, 0);
    else                   prefix.writeUInt16BE(charCount & 0xFFFF, 0);
    return Buffer.concat([prefix, encoded]);
}

// ─── rebuild pool + update record pool offsets ────────────────────────────────
function rebuildPool(table, editedStrings, huffRoot, huffmanEnd) {
    // editedStrings: Map< `${recIdx}:${fieldName}` → newString >

    const { dataBody, lengthBytes, currentRecords, extraData, fieldDefs } = table;
    const recordsBuf = Buffer.from(dataBody.slice(0, table.extraDataOffset));   // mutable copy

    // Huffman tree portion (up to the first string)
    const treeBytes = extraData.slice(0, huffmanEnd);
    const poolParts = [treeBytes];
    let runningOffset = treeBytes.length;   // pool offset of next field

    // Walk records × varchar fields in the same order they appear in the pool
    for (let i = 0; i < currentRecords; i++) {
        const rawSlice = recordsBuf.slice(i * lengthBytes, (i + 1) * lengthBytes);

        for (const def of fieldDefs) {
            if (def.type !== FTYPE_VARCHAR1 && def.type !== FTYPE_VARCHAR2) continue;

            const prefixBytes = def.type === FTYPE_VARCHAR1 ? 1 : 2;
            const key = `${i}:${def.name}`;
            const oldOffset = readUInt32LE(rawSlice, def.offset >> 3);

            // 0xFFFFFFFF is a null sentinel — no string exists for this record.
            // Preserve the value as-is and do not add anything to the pool.
            if (!editedStrings.has(key) && oldOffset === 0xFFFFFFFF) {
                continue;
            }

            let encoded;
            if (editedStrings.has(key)) {
                // Re-encode the edited string
                const newStr = editedStrings.get(key);
                encoded = encodeString(huffRoot, newStr, prefixBytes);
            } else {
                // Keep original bytes from pool
                const nextKey = nextPoolOffset(table, i, def, fieldDefs, currentRecords);
                const end     = nextKey !== null ? nextKey : extraData.length;
                encoded = extraData.slice(oldOffset, end);
            }

            // Write new pool offset into record raw bytes (LE uint32)
            writeUInt32LE(rawSlice, runningOffset, def.offset >> 3);
            poolParts.push(encoded);
            runningOffset += encoded.length;
        }
    }

    return { recordsBuf, pool: Buffer.concat(poolParts) };
}

/** Return the pool offset of the NEXT non-null varchar field after (recIdx, def), or null. */
function nextPoolOffset(table, recIdx, curDef, fieldDefs, currentRecords) {
    const { dataBody, lengthBytes } = table;
    const varcharDefs = fieldDefs.filter(d => d.type === FTYPE_VARCHAR1 || d.type === FTYPE_VARCHAR2);
    const curPos = varcharDefs.indexOf(curDef);
    // Move to next varchar in same record, or first varchar of next record
    let nextRec = recIdx, nextPos = curPos + 1;
    if (nextPos >= varcharDefs.length) { nextRec++; nextPos = 0; }
    // Skip null sentinel entries (0xFFFFFFFF) — they have no bytes in the pool
    while (nextRec < currentRecords) {
        const nextDef = varcharDefs[nextPos];
        const raw = dataBody.slice(nextRec * lengthBytes, (nextRec + 1) * lengthBytes);
        const offset = readUInt32LE(raw, nextDef.offset >> 3);
        if (offset !== 0xFFFFFFFF) return offset;
        nextPos++;
        if (nextPos >= varcharDefs.length) { nextRec++; nextPos = 0; }
    }
    return null;
}

// ─── CRC helpers (mirrors _calc_checksums in the Python code) ─────────────────
/**
 * We don't need to fully reimplement CRC—instead we reuse the TDBWriter for
 * writing after patching the data buffers in the parsed TDBHelper file object.
 * But since we bypassed TDBHelper, we write manually and use the CRC service.
 */
const crcSvc = new CRC();
function crc32be(buf) {
    return crcSvc.crc32_be(0, buf, buf.length);
}

// ─── write rebuilt DB to disk ─────────────────────────────────────────────────
function saveDB(db, table, recordsBuf, pool, destPath) {
    const { data, dbSize, dataStart } = db;

    // Build the new file as a mutable copy of the original
    const out = Buffer.from(data);

    // Overwrite record data + pool in-place
    const bodyStart = table.dataBodyStart;
    recordsBuf.copy(out, bodyStart);
    pool.copy(out, bodyStart + table.extraDataOffset);

    // Recalculate CRCs (same logic as Python _calc_checksums):
    // 1. Header CRC at offset 20 over bytes [0..19]
    // 2. Table prior CRC chain
    // 3. EOF CRC at dbSize-4

    // Header CRC
    const headerCrc = (~crc32be(out.slice(0, 20))) >>> 0;
    out.writeUInt32LE(headerCrc, 20);

    // Table index CRC → priorCrc for first table's header slot
    const indexBuf = out.slice(HEADER_SIZE, dataStart);
    let prior = (~crc32be(indexBuf)) >>> 0;

    for (let i = 0; i < db.tables.length; i++) {
        const t = db.tables[i];
        const hp = t.hp;

        // Table header CRC (bytes hp+4 .. hp+35, i.e. INFO_SIZE-8 = 32 bytes)
        const hdrCrc = (~crc32be(out.slice(hp + 4, hp + 36))) >>> 0;
        out.writeUInt32LE(prior,  hp);       // prior CRC
        out.writeUInt32LE(hdrCrc, hp + 36);  // header CRC

        // Data CRC for this table (from hp+40 to start of next table or dbSize-4)
        const dataBodyEnd = i < db.tables.length - 1
            ? dataStart + db.tables[i + 1].defOffset
            : dbSize - 4;
        prior = (~crc32be(out.slice(t.dataBodyStart, dataBodyEnd))) >>> 0;
    }

    // EOF CRC
    out.writeUInt32LE(prior, dbSize - 4);

    fs.writeFileSync(destPath, out);
}

// ─── main ─────────────────────────────────────────────────────────────────────
async function main() {
    let args;
    try { args = JSON.parse(process.argv[2]); }
    catch (e) { return respond({ ok: false, error: 'Bad args JSON: ' + e.message }); }

    const { command, db_path } = args;

    if (!db_path || !fs.existsSync(db_path)) {
        return respond({ ok: false, error: 'DB file not found: ' + db_path });
    }

    // ── convert (xbox-to-pc or pc-to-xbox) — handled before parseDB ──────────
    // parseDB always reads LE, so Xbox (BE) files must be converted first.
    if (command === 'convert') {
        const direction = args.direction || 'xbox-to-pc';
        const destPath  = args.dest_path;
        if (!destPath) return respond({ ok: false, error: 'dest_path required' });

        const srcBE = (direction === 'xbox-to-pc');   // source is BE when converting from Xbox
        const dstBE = !srcBE;

        const srcU32  = srcBE ? (b,o) => b.readUInt32BE(o) : (b,o) => b.readUInt32LE(o);
        const srcU16  = srcBE ? (b,o) => b.readUInt16BE(o) : (b,o) => b.readUInt16LE(o);
        const dstU32  = dstBE ? (b,v,o) => b.writeUInt32BE(v>>>0,o) : (b,v,o) => b.writeUInt32LE(v>>>0,o);
        const dstU16  = dstBE ? (b,v,o) => b.writeUInt16BE(v,o)     : (b,v,o) => b.writeUInt16LE(v,o);

        // 4-char names: in BE files bytes are reversed compared to PC
        const srcName = srcBE
            ? (buf,o) => String.fromCharCode(buf[o+3],buf[o+2],buf[o+1],buf[o])
            : (buf,o) => String.fromCharCode(buf[o],buf[o+1],buf[o+2],buf[o+3]);
        const dstName = dstBE
            ? (buf,name,o) => { buf[o]=name.charCodeAt(3); buf[o+1]=name.charCodeAt(2); buf[o+2]=name.charCodeAt(1); buf[o+3]=name.charCodeAt(0); }
            : (buf,name,o) => { buf[o]=name.charCodeAt(0); buf[o+1]=name.charCodeAt(1); buf[o+2]=name.charCodeAt(2); buf[o+3]=name.charCodeAt(3); };

        const raw = fs.readFileSync(db_path);
        const out = Buffer.from(raw);

        // 1. Endian byte
        out[4] = dstBE ? 1 : 0;

        // 2. Header uint32s
        const dbSz  = srcU32(raw, 8);
        const h12   = srcU32(raw, 12);
        const tblCt = srcU32(raw, 16);
        dstU32(out, dbSz, 8);
        dstU32(out, h12,  12);
        dstU32(out, tblCt, 16);

        // 3. Table index entries: name + offset
        const convDefs = [];
        let ip = HEADER_SIZE;
        for (let i = 0; i < tblCt; i++) {
            const name   = srcName(raw, ip);
            const offset = srcU32(raw, ip + 4);
            convDefs.push({ name, offset });
            dstName(out, name, ip);
            dstU32(out, offset, ip + 4);
            ip += TABLE_DEF_ENTRY;
        }
        convDefs.sort((a,b) => a.offset - b.offset);
        const dStart = ip;   // dataStart

        // 4. Per-table conversion
        for (const def of convDefs) {
            const hp2 = dStart + def.offset;

            // Table header multi-byte fields (skip uint8s at hp+28,29)
            const dAt = srcU32(raw, hp2+4);   const lB = srcU32(raw, hp2+8);
            const h12t = srcU32(raw, hp2+12); const h16t = srcU32(raw, hp2+16);
            const mxR  = srcU16(raw, hp2+20); const crR  = srcU16(raw, hp2+22);
            const h24t = srcU32(raw, hp2+24); const h30t = srcU16(raw, hp2+30);
            const h32t = srcU32(raw, hp2+32);

            dstU32(out, dAt,  hp2+4);  dstU32(out, lB,   hp2+8);
            dstU32(out, h12t, hp2+12); dstU32(out, h16t, hp2+16);
            dstU16(out, mxR,  hp2+20); dstU16(out, crR,  hp2+22);
            dstU32(out, h24t, hp2+24); dstU16(out, h30t, hp2+30);
            dstU32(out, h32t, hp2+32);

            const nF    = raw[hp2 + 28];
            const fs2   = hp2 + TABLE_HEADER_SZ;
            const dbs   = fs2 + nF * FIELD_DEF_SZ;

            // Field definitions
            const fds = [];
            for (let i = 0; i < nF; i++) {
                const fp2  = fs2 + i * FIELD_DEF_SZ;
                const ft   = srcU32(raw, fp2);
                const bOff = srcU32(raw, fp2+4);
                const fn   = srcName(raw, fp2+8);
                const bi   = srcU32(raw, fp2+12);
                fds.push({ type: ft, offset: bOff });
                dstU32(out, ft,  fp2);
                dstU32(out, bOff, fp2+4);
                dstName(out, fn, fp2+8);
                dstU32(out, bi,  fp2+12);
            }

            // Records: swap uint32 values at byte-aligned field offsets
            const lenB = lB;
            const crRn = crR;
            for (let i = 0; i < crRn; i++) {
                const rOff = dbs + i * lenB;
                for (const fd of fds) {
                    const bOff = fd.offset >> 3;
                    // All int/float/varchar types store uint32 at byte-aligned offsets
                    if (fd.type !== FTYPE_STRING && fd.type !== FTYPE_BINARY) {
                        const v = srcU32(raw, rOff + bOff);
                        dstU32(out, v, rOff + bOff);
                    }
                }
            }
            // Extra data (Huffman pool): copy as-is — bit-level encoding is endian-independent
            // VARCHAR2 prefix is always 2-byte BE — no change needed
        }

        // 5. Recalculate CRCs in destination endianness
        const wU32 = dstBE ? (v,o) => out.writeUInt32BE(v>>>0,o) : (v,o) => out.writeUInt32LE(v>>>0,o);
        const rU32 = dstBE ? (o) => out.readUInt32BE(o) : (o) => out.readUInt32LE(o);

        const hCrc = (~crc32be(out.slice(0, 20))) >>> 0;
        wU32(hCrc, 20);

        let prior = (~crc32be(out.slice(HEADER_SIZE, dStart))) >>> 0;
        const finalDbSz = rU32(8);

        for (let i = 0; i < convDefs.length; i++) {
            const hp2 = dStart + convDefs[i].offset;
            // Data CRC covers field defs + records + pool (starts at hp+TABLE_HEADER_SZ)
            const fieldStart2 = hp2 + TABLE_HEADER_SZ;
            const hCrc2 = (~crc32be(out.slice(hp2+4, hp2+36))) >>> 0;
            wU32(prior,  hp2);
            wU32(hCrc2,  hp2+36);
            const dBodyEnd = i < convDefs.length-1
                ? dStart + convDefs[i+1].offset
                : finalDbSz - 4;
            prior = (~crc32be(out.slice(fieldStart2, dBodyEnd))) >>> 0;
        }
        wU32(prior, finalDbSz - 4);

        try {
            const tmp = destPath + '.tmp_conv';
            fs.writeFileSync(tmp, out);
            fs.renameSync(tmp, destPath);
        } catch (e) {
            return respond({ ok: false, error: 'Write failed: ' + e.message });
        }

        return respond({ ok: true });
    }

    // ── list / apply-edits — parse the LE database ────────────────────────────
    let db;
    try { db = parseDB(fs.readFileSync(db_path)); }
    catch (e) { return respond({ ok: false, error: 'Parse failed: ' + e.message }); }

    const tableName = args.table_name || null;
    const table = tableName
        ? db.tables.find(t => t.name === tableName)
        : db.tables[0];

    if (!table) {
        return respond({ ok: false, error: 'Table not found: ' + (tableName || '(first)') });
    }

    // ── Huffman setup ────────────────────────────────────────────────────────
    let huffRoot = null, huffmanEnd = 0;
    const hasHuffman = table.dataAllocationType === 66;
    if (hasHuffman) {
        huffmanEnd = findHuffmanEnd(table);
        const treeBuf = table.extraData.slice(0, huffmanEnd);
        try { huffRoot = huffmanTreeParser.parseTree(treeBuf); }
        catch (e) { return respond({ ok: false, error: 'Huffman tree parse failed: ' + e.message }); }
    }

    // ── list ─────────────────────────────────────────────────────────────────
    if (command === 'list') {
        const fieldDefs = table.fieldDefs.map(d => ({ name: d.name, type: d.type }));
        const records = [];

        for (let i = 0; i < table.currentRecords; i++) {
            const raw = table.dataBody.slice(i * table.lengthBytes, (i + 1) * table.lengthBytes);
            const fields = {};
            for (const def of table.fieldDefs) {
                const v = readField(raw, def, table.extraData, huffRoot);
                fields[def.name] = String(v);
            }
            records.push({ index: i, fields });
        }

        return respond({ ok: true, fieldDefs, records });
    }

    // ── apply-edits ───────────────────────────────────────────────────────────
    if (command === 'apply-edits') {
        const edits  = args.edits || [];
        const errors = [];
        const destPath = args.dest_path || db_path;

        if (!hasHuffman) {
            const newData = Buffer.from(db.data);
            for (const edit of edits) {
                const def = table.fieldDefs.find(d => d.name === edit.field);
                if (!def) { errors.push(`Field '${edit.field}' not found`); continue; }
                if (def.type === FTYPE_VARCHAR1 || def.type === FTYPE_VARCHAR2 || def.type === FTYPE_STRING) {
                    errors.push(`String edits not supported for this DB type`); continue;
                }
                const byteOff = table.dataBodyStart + edit.index * table.lengthBytes + (def.offset >> 3);
                newData.writeUInt32LE(parseInt(edit.value) >>> 0, byteOff);
            }
            if (errors.length) return respond({ ok: false, error: errors.join('; ') });
            const tmp = destPath + '.tmp_bridge';
            fs.writeFileSync(tmp, newData);
            fs.renameSync(tmp, destPath);
            return respond({ ok: true });
        }

        const editedStrings = new Map();
        const intEdits = [];

        for (const edit of edits) {
            const def = table.fieldDefs.find(d => d.name === edit.field);
            if (!def) { errors.push(`Field '${edit.field}' not found`); continue; }
            if (def.type === FTYPE_VARCHAR1 || def.type === FTYPE_VARCHAR2) {
                editedStrings.set(`${edit.index}:${edit.field}`, edit.value);
            } else {
                intEdits.push({ ...edit, def });
            }
        }

        if (errors.length) return respond({ ok: false, error: errors.join('; ') });

        let recordsBuf, pool;
        try {
            ({ recordsBuf, pool } = rebuildPool(table, editedStrings, huffRoot, huffmanEnd));
        } catch (e) {
            return respond({ ok: false, error: 'Pool rebuild failed: ' + e.message });
        }

        for (const edit of intEdits) {
            const byteOff = edit.index * table.lengthBytes + (edit.def.offset >> 3);
            writeUInt32LE(recordsBuf, parseInt(edit.value) >>> 0, byteOff);
        }

        const tmp = destPath + '.tmp_bridge';
        try {
            saveDB(db, table, recordsBuf, pool, tmp);
            fs.renameSync(tmp, destPath);
        } catch (e) {
            try { fs.unlinkSync(tmp); } catch (_) {}
            return respond({ ok: false, error: 'Save failed: ' + e.message });
        }

        return respond({ ok: true });
    }

    return respond({ ok: false, error: 'Unknown command: ' + command });
}

main().catch(e => {
    respond({ ok: false, error: e.message, stack: e.stack });
    process.exit(1);
});
