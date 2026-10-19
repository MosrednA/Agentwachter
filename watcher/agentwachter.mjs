#!/usr/bin/env node
// Agentwachter, phase 1: a passive watcher for running Claude Code and Codex sessions.
// It only reads what both tools already write to disk, so watching costs no turns and no limit.
//
//   node agentwachter.mjs                      serve http://127.0.0.1:17321/agents (JSON) and / (HTML)
//   node agentwachter.mjs --once               print the JSON once and exit
//   node agentwachter.mjs --replay <file>      per-hour heuristics for a Codex rollout or Claude transcript
//   node agentwachter.mjs --demo <file>        serve a fixed snapshot instead of live sessions

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { execFile } from 'node:child_process';
import { StringDecoder } from 'node:string_decoder';
import { DatabaseSync } from 'node:sqlite';

const HOME = os.homedir();
const CLAUDE = path.join(HOME, '.claude');
const CODEX = path.join(HOME, '.codex');
const BRIDGE_LOG = path.join(HOME, '.agents', 'inbox', 'log.jsonl');
// Above Windows' dynamic port range. WSL in mirrored networking mode reserves a moving 4096-port block,
// so any fixed port can be taken; AGENTWACHTER_PORT moves it (the tray in tray/ reads the same variable).
const PORT = Number(process.env.AGENTWACHTER_PORT) || 17321;

const MIN = 60_000, HOUR = 60 * MIN;
const REFRESH = 30_000;
const CODEX_RECENT = (Number(process.env.AGENTWACHTER_CODEX_HOURS) || 12) * HOUR; // Codex threads older than this are left out
const QUIET = 10 * MIN;  // working, but nothing written for this long: quiet
const ORPHAN = HOUR;     // a Codex turn "in progress" that has written nothing for this long: dead
const DONE_KEEP = (Number(process.env.AGENTWACHTER_DONE_HOURS) || 1) * HOUR; // done sessions idle longer than this are left out
// Going in circles: busy, mostly re-running what it already ran this hour, and not touching any new file.
const CIRCLES = { toolsPerHour: 40, repeatRatio: 0.5 };

const STATES = ['waiting', 'busy', 'quiet', 'done', 'dead'];
const EDIT_TOOLS = new Set(['Edit', 'MultiEdit', 'Write', 'NotebookEdit']);

// ---------- incremental reading ----------

/** Follows a growing JSONL file from a byte offset. Node opens files with full sharing, so writers are never blocked. */
class Tail {
  offset = 0;
  rest = '';
  decoder = new StringDecoder('utf8');

  constructor(file) { this.file = file; }

  /** Calls onLine for each new complete line. Returns false when the file shrank (rewritten): start over. */
  read(onLine) {
    let size;
    try { size = fs.statSync(this.file).size; } catch { return true; }
    if (size < this.offset) return false;
    if (size === this.offset) return true;
    const fd = fs.openSync(this.file, 'r');
    try {
      const buf = Buffer.allocUnsafe(4 << 20);
      while (this.offset < size) {
        const n = fs.readSync(fd, buf, 0, Math.min(buf.length, size - this.offset), this.offset);
        if (n <= 0) break;
        this.offset += n;
        const lines = (this.rest + this.decoder.write(buf.subarray(0, n))).split('\n');
        this.rest = lines.pop();
        for (const line of lines) if (line.length > 1) onLine(line);
      }
    } finally {
      fs.closeSync(fd);
    }
    return true;
  }
}

// ---------- per-session activity ----------

class Stats {
  tools = [];             // { t, fp, err } in time order
  tokens = [];            // { t, n }
  patches = [];           // times a file was patched
  firstPatch = new Map(); // path -> first time it was patched
  lastPatch = 0;
  lastAt = 0;             // newest record
  text = '';              // first line of the last assistant text
  // Claude
  msgIds = new Set();
  pending = new Map();    // tool_use id -> tool event, until its result arrives
  ended = false;          // subagent: its last record ends its turn (handed back, or interrupted)
  meta = null;            // subagent: its type and description
  // Codex
  tokenTotal = 0;
  turnOpen = undefined;   // from task_started / task_complete in the rollout
  pendingInput = null;    // a request_user_input call still without output
  items = false;          // rollout has item_completed events (newer Codex); else fall back to raw calls

  tool(t, fp, err = false, src = 'item') {
    const e = { t, fp: fingerprint(fp), err, src };
    this.tools.push(e);
    return e;
  }

  patch(t, files) {
    this.patches.push(t);
    this.lastPatch = Math.max(this.lastPatch, t);
    for (const f of files) if (!this.firstPatch.has(f)) this.firstPatch.set(f, t);
  }

  setText(text) {
    const line = String(text).split('\n').map(l => l.replace(/^[\s#>*\-]+/, '').trim()).find(Boolean);
    if (line) this.text = line.replace(/\s+/g, ' ').slice(0, 160);
  }

  prune(before) {
    this.tools = this.tools.filter(e => e.t >= before);
    this.tokens = this.tokens.filter(e => e.t >= before);
    this.patches = this.patches.filter(t => t >= before);
    if (this.msgIds.size > 2000) this.msgIds.clear();
    if (this.pending.size > 500) this.pending.clear();
  }
}

/** Digits become N so "sleep 30; tail -n 200 run-17.log" and "sleep 45; tail -n 200 run-18.log" count as the same call. */
const fingerprint = s => String(s).replace(/\d+/g, 'N').replace(/\s+/g, ' ').trim().slice(0, 400);

/** Heuristics over the hour before `now`. */
function metrics(s, now) {
  const from = now - HOUR;
  const seen = new Map();
  let tools = 0, errs = 0, repeats = 0;
  for (const e of s.tools) {
    if (e.t > now) break;
    if (e.src === 'resp' && s.items) continue; // Codex item events already cover these calls
    if (e.t > from) {
      tools++;
      if (e.err) errs++;
      const prev = seen.get(e.fp);
      if (prev !== undefined && e.t - prev <= HOUR) repeats++;
    }
    seen.set(e.fp, e.t);
  }
  let tokens = 0;
  for (const e of s.tokens) if (e.t > from && e.t <= now) tokens += e.n;
  let newFiles = 0;
  for (const t of s.firstPatch.values()) if (t > from && t <= now) newFiles++;
  const lastPatch = s.patches.findLast(t => t <= now) ?? (s.lastPatch && s.lastPatch <= now ? s.lastPatch : null);
  const repeatRatio = tools ? repeats / tools : null;
  return {
    tokensPerHour: tokens,
    toolsPerHour: tools,
    errorRate: tools ? round(errs / tools) : null,
    repeatRatio: repeatRatio === null ? null : round(repeatRatio),
    newFiles,
    minsSincePatch: lastPatch ? Math.round((now - lastPatch) / MIN) : null,
    circles: tools >= CIRCLES.toolsPerHour && repeatRatio >= CIRCLES.repeatRatio && newFiles === 0,
  };
}

const round = x => Math.round(x * 100) / 100;

// ---------- Claude Code transcripts ----------

/** `sidechain`: the line is from a subagent's own transcript, where every record is a sidechain. */
function claudeLine(s, line, sidechain = false) {
  let o;
  try { o = JSON.parse(line); } catch { return; }
  const t = Date.parse(o.timestamp);
  const m = o.message;
  if (!t || (o.isSidechain && !sidechain) || !m) return;
  if (o.type === 'assistant') {
    s.lastAt = Math.max(s.lastAt, t);
    s.ended = m.stop_reason === 'end_turn' && !(m.content ?? []).some(c => c.type === 'tool_use');
    // One API message is written as several records (one per content block), each repeating its usage.
    if (m.id && !s.msgIds.has(m.id) && m.usage) {
      s.msgIds.add(m.id);
      const u = m.usage;
      s.tokens.push({ t, n: (u.input_tokens || 0) + (u.cache_creation_input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.output_tokens || 0) });
    }
    for (const c of m.content ?? []) {
      if (c.type === 'text' && c.text?.trim()) s.setText(c.text);
      if (c.type !== 'tool_use') continue;
      // Shell calls by their command alone; the free-text description differs per call.
      s.pending.set(c.id, s.tool(t, `${c.name} ${c.input?.command ?? JSON.stringify(c.input ?? {})}`));
      const file = c.input?.file_path ?? c.input?.notebook_path;
      if (EDIT_TOOLS.has(c.name) && file) s.patch(t, [file]);
    }
  } else if (o.type === 'user') {
    s.lastAt = Math.max(s.lastAt, t);
    // A subagent hands back with a tool call that ends its turn; a new message resumes it.
    const text = typeof m.content === 'string' ? m.content : m.content?.find?.(c => c.type === 'text')?.text ?? '';
    s.ended = !!o.toolEndsTurn || text.startsWith('[Request interrupted');
    for (const c of Array.isArray(m.content) ? m.content : []) {
      if (c.type !== 'tool_result') continue;
      const e = s.pending.get(c.tool_use_id);
      s.pending.delete(c.tool_use_id);
      if (e && looksLikeError(c)) e.err = true;
    }
  }
}

function looksLikeError(result) {
  if (result.is_error) return true;
  const text = typeof result.content === 'string' ? result.content
    : Array.isArray(result.content) ? result.content.map(c => c.text ?? '').join('') : '';
  return /^\s*(<tool_use_error>|Error:|Exit code [1-9])/.test(text);
}

// ---------- Codex rollouts ----------

function codexLine(s, line) {
  // Skip the big lines we never need (encrypted reasoning, context snapshots) without parsing them.
  const head = line.slice(0, 200);
  if (!head.includes('"event_msg"') && !head.includes('"response_item"')) return;
  if (head.includes('"payload":{"type":"reasoning"')) return;
  let o;
  try { o = JSON.parse(line); } catch { return; }
  const t = Date.parse(o.timestamp);
  const p = o.payload;
  if (!t || !p) return;
  s.lastAt = Math.max(s.lastAt, t);

  if (o.type === 'event_msg') {
    switch (p.type) {
      case 'item_completed': codexItem(s, t, p.item ?? {}); break;
      case 'token_count': {
        const total = p.info?.total_token_usage?.total_tokens;
        if (typeof total !== 'number') break;
        if (total > s.tokenTotal) s.tokens.push({ t, n: total - s.tokenTotal });
        s.tokenTotal = total;
        break;
      }
      case 'task_started': s.turnOpen = true; s.pendingInput = null; break;
      case 'task_complete': s.turnOpen = false; if (p.last_agent_message) s.setText(p.last_agent_message); break;
      case 'turn_aborted': s.turnOpen = false; break;
    }
    return;
  }

  // Raw calls: only counted for rollouts without item events, but always used to spot a pending question.
  switch (p.type) {
    case 'function_call':
    case 'custom_tool_call': {
      if (/request_user_input/.test(p.name)) s.pendingInput = p.call_id;
      const e = s.tool(t, `${p.name} ${p.arguments ?? p.input ?? ''}`, false, 'resp');
      s.pending.set(p.call_id, e);
      if (p.name === 'apply_patch') s.patch(t, [...String(p.input ?? p.arguments).matchAll(/\*\*\* (?:Add|Update|Delete) File: (.+)/g)].map(m => m[1].trim()));
      break;
    }
    case 'function_call_output':
    case 'custom_tool_call_output': {
      if (p.call_id === s.pendingInput) s.pendingInput = null;
      const e = s.pending.get(p.call_id);
      s.pending.delete(p.call_id);
      const out = typeof p.output === 'string' ? p.output : JSON.stringify(p.output ?? '');
      if (e && /Exit code: [1-9]|exit_code\\?":\s*[1-9]|Process exited with code [1-9]/.test(out.slice(0, 4000))) e.err = true;
      break;
    }
    case 'message':
      if (p.role === 'assistant' && !s.items) s.setText((p.content ?? []).map(c => c.text ?? '').join(''));
      break;
  }
}

function codexItem(s, t, it) {
  s.items = true;
  switch (it.type) {
    case 'CommandExecution':
      s.tool(t, `cmd ${Array.isArray(it.command) ? it.command.at(-1) : it.command}`, it.status === 'failed' || (it.exit_code ?? 0) !== 0);
      break;
    case 'McpToolCall':
      s.tool(t, `mcp ${it.server}.${it.tool} ${JSON.stringify(it.arguments ?? '')}`, it.status === 'failed' || !!it.result?.isError);
      break;
    case 'FileChange': {
      const files = Object.keys(it.changes ?? {});
      s.tool(t, `patch ${files.join(' ')} ${JSON.stringify(it.changes ?? '').slice(0, 300)}`, it.status === 'failed');
      s.patch(t, files);
      break;
    }
    case 'Extension': s.tool(t, `${it.kind} ${it.query ?? ''}`); break;
    case 'ImageView': s.tool(t, `view ${it.path}`); break;
    case 'CollabAgentToolCall': if (it.tool !== 'wait') s.tool(t, `agent ${it.tool}`, it.status === 'failed'); break;
    case 'AgentMessage': s.setText((it.content ?? []).map(c => c.text ?? '').join('')); break;
  }
}

// ---------- sources ----------

const stripLong = p => String(p ?? '').replace(/^\\\\\?\\/, '');

/** "C:\Projects\bb-core\.claude\worktrees\x" -> "bb-core". */
function project(cwd) {
  const p = stripLong(cwd).replace(/[\\/]\.claude[\\/]worktrees[\\/].*$/, '').replace(/[\\/]+$/, '');
  return path.basename(p) || p;
}

/**
 * Maps a folder to its project, using the folders sessions run in as roots, so an agent message sent
 * from "...\mossub\results\x" counts for mossub. Roots that hold other roots (a parent folder) are skipped.
 */
function projectResolver(cwds) {
  const clean = c => stripLong(c).replace(/[\\/]+$/, '');
  const inside = (p, root) => p.startsWith(root + '\\') || p.startsWith(root + '/');
  const roots = [...new Set(cwds.map(c => clean(c).toLowerCase()))];
  const leaves = roots.filter(r => !roots.some(o => inside(o, r)));
  return cwd => {
    const p = clean(cwd);
    const root = leaves.filter(r => inside(p.toLowerCase(), r)).sort((a, b) => b.length - a.length)[0];
    return project(root ? p.slice(0, root.length) : p);
  };
}

/** pid -> image name, or null when the process list is unavailable. */
function processes() {
  if (process.platform !== 'win32') return Promise.resolve(null);
  return new Promise(resolve => execFile('tasklist', ['/FO', 'CSV', '/NH'], { windowsHide: true, maxBuffer: 8 << 20 }, (err, out) => {
    if (err) return resolve(null);
    const map = new Map();
    for (const line of out.split(/\r?\n/)) {
      const m = /^"([^"]*)","(\d+)"/.exec(line);
      if (m) map.set(Number(m[2]), m[1]);
    }
    resolve(map);
  }));
}

function pidAlive(pid, procs) {
  if (procs) return /claude|node|bun/i.test(procs.get(pid) ?? ''); // also rules out most reused pids
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

function claudeSessions(procs) {
  const dir = path.join(CLAUDE, 'sessions');
  let files = [];
  try { files = fs.readdirSync(dir).filter(f => /^\d+\.json$/.test(f)); } catch { return []; }
  const out = [];
  for (const f of files) {
    try {
      const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
      if (j.sessionId && pidAlive(j.pid, procs)) out.push(j);
    } catch { /* being rewritten */ }
  }
  return out;
}

function claudeTranscript(session) {
  const name = `${session.sessionId}.jsonl`;
  const guess = path.join(CLAUDE, 'projects', String(session.cwd).replace(/[^A-Za-z0-9-]/g, '-'), name);
  if (fs.existsSync(guess)) return guess;
  try {
    for (const d of fs.readdirSync(path.join(CLAUDE, 'projects'))) {
      const p = path.join(CLAUDE, 'projects', d, name);
      if (fs.existsSync(p)) return p;
    }
  } catch { /* no projects folder */ }
  return null;
}

function openDb(file) {
  const p = path.join(CODEX, file);
  return fs.existsSync(p) ? new DatabaseSync(p, { readOnly: true }) : null;
}

/**
 * Recent top-level Codex threads with their last turn, the agents they spawned that were active in the past hour,
 * and every folder Codex has worked in. Short reads; both databases are closed again right away.
 */
function codexThreads(now) {
  let state, history;
  try {
    state = openDb('state_5.sqlite');
    if (!state) return { threads: [], subagents: [], cwds: [] };
    history = openDb('thread_history_1.sqlite');
    const top = `source NOT LIKE '{%' AND coalesce(thread_source, '') NOT IN ('subagent', 'guardian_review')`;
    const rows = state.prepare(`
      SELECT id, rollout_path, cwd, title, name, tokens_used, updated_at
      FROM threads WHERE archived = 0 AND updated_at >= ? AND ${top}
      ORDER BY updated_at DESC`).all(Math.floor((now - CODEX_RECENT) / 1000));
    // Spawned agents only; guardian reviews are approval checks, not work.
    const spawned = state.prepare(`
      SELECT id, rollout_path, source, agent_nickname, agent_path
      FROM threads WHERE archived = 0 AND updated_at >= ? AND source LIKE '{"subagent":{"thread_spawn"%'`)
      .all(Math.floor((now - ORPHAN) / 1000));
    const cwds = state.prepare(`SELECT DISTINCT cwd FROM threads WHERE ${top}`).all().map(r => r.cwd);
    const turn = history?.prepare(`
      SELECT status, started_at, completed_at FROM thread_turns
      WHERE thread_id = ? ORDER BY started_at DESC, rollout_ordinal DESC LIMIT 1`);
    const subagents = [];
    for (const r of spawned) {
      let parent;
      try { parent = JSON.parse(r.source).subagent.thread_spawn.parent_thread_id; } catch { continue; }
      if (parent) subagents.push({ ...r, parent, turn: turn?.get(r.id) });
    }
    return { threads: rows.map(r => ({ ...r, turn: turn?.get(r.id) })), subagents, cwds };
  } catch (e) {
    console.error('codex:', e.message);
    return { threads: [], subagents: [], cwds: [] };
  } finally {
    state?.close();
    history?.close();
  }
}

/** Agent-to-agent messages from the agent-messaging log, per session and per folder. */
class Bridge {
  tail = new Tail(BRIDGE_LOG);
  sessions = new Map(); // session or thread id -> { sent: [t], received: [t] }
  cwds = new Map();     // folder -> same

  read() {
    if (!this.tail.read(line => this.add(line))) {
      this.tail = new Tail(BRIDGE_LOG);
      this.sessions.clear();
      this.cwds.clear();
      this.tail.read(line => this.add(line));
    }
  }

  add(line) {
    let e;
    try { e = JSON.parse(line); } catch { return; }
    const t = Date.parse(e.sent) || 0;
    const bump = (map, key, dir) => {
      if (!key) return;
      if (!map.has(key)) map.set(key, { sent: [], received: [] });
      map.get(key)[dir].push(t);
    };
    bump(this.sessions, e.from?.thread, 'sent');
    bump(this.sessions, e.to?.id, 'received');
    bump(this.cwds, e.from?.cwd, 'sent');
    bump(this.cwds, e.to?.cwd, 'received');
  }

  projects(resolve, now) {
    const by = new Map();
    for (const [cwd, e] of this.cwds) {
      const name = resolve(cwd);
      if (!by.has(name)) by.set(name, { sent: [], received: [] });
      by.get(name).sent.push(...e.sent);
      by.get(name).received.push(...e.received);
    }
    return [...by].map(([name, e]) => ({ name, bridge: Bridge.summary(e, now) }))
      .sort((a, b) => Date.parse(b.bridge.last) - Date.parse(a.bridge.last));
  }

  static summary(entry, now) {
    if (!entry) return { sent: 0, received: 0, lastHour: 0, last: null };
    const all = [...entry.sent, ...entry.received];
    const last = Math.max(...all);
    return { sent: entry.sent.length, received: entry.received.length, lastHour: all.filter(t => t > now - HOUR).length, last: iso(last) };
  }
}

const iso = t => (t ? new Date(t).toISOString() : null);

// ---------- live watching ----------

const followed = new Map(); // key -> { file, tail, stats }
const bridge = new Bridge();
let snapshot = { updated: null, sessions: [], projects: [] };

/** Reads what is new in a session's log and returns its stats. */
function follow(key, file, parse, now) {
  let f = followed.get(key);
  if (!f || f.file !== file) followed.set(key, f = { file, tail: new Tail(file), stats: new Stats() });
  if (!f.tail.read(line => parse(f.stats, line))) {
    Object.assign(f, { tail: new Tail(file), stats: new Stats() });
    f.tail.read(line => parse(f.stats, line));
  }
  f.stats.prune(now - 2 * HOUR);
  return f.stats;
}

function mtime(file) {
  try { return fs.statSync(file).mtimeMs; } catch { return 0; }
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

const subagent = (s, last, state, name, title, now) => ({ name, title, state, summary: s.text, lastActivity: iso(last), ...metrics(s, now) });
const claudeSidechain = (s, line) => claudeLine(s, line, true);

/**
 * A Claude session's subagents active in the past hour. Each writes its own transcript next to the session's,
 * <session>/subagents/agent-<id>.jsonl, with its type and description in agent-<id>.meta.json.
 */
function claudeSubagents(file, sessionId, now, keys) {
  const dir = path.join(path.dirname(file), sessionId, 'subagents');
  let names;
  try { names = fs.readdirSync(dir).filter(f => f.endsWith('.jsonl')); } catch { return []; }
  const out = [];
  for (const name of names) {
    const sub = path.join(dir, name);
    const changed = mtime(sub);
    if (now - changed > ORPHAN) continue;
    const key = `claude:${sessionId}:${name}`;
    keys.add(key);
    const s = follow(key, sub, claudeSidechain, now);
    s.meta ??= readJson(sub.replace(/\.jsonl$/, '.meta.json'));
    const last = Math.max(s.lastAt, changed);
    const state = s.ended ? 'done' : now - last > QUIET ? 'quiet' : 'busy';
    out.push(subagent(s, last, state, s.meta?.agentType || 'agent', s.meta?.description ?? '', now));
  }
  return out;
}

/** Codex agents spawned in the past hour, per top-level thread. An agent spawned by an agent counts for the thread at the top. */
function codexSubagents(rows, now, keys) {
  const parentOf = new Map(rows.map(r => [r.id, r.parent]));
  const by = new Map();
  for (const r of rows) {
    let top = r.parent;
    for (let i = 0; i < 10 && parentOf.has(top); i++) top = parentOf.get(top);
    const file = stripLong(r.rollout_path);
    const key = `codex:${r.id}`;
    keys.add(key);
    const s = follow(key, file, codexLine, now);
    const last = Math.max(s.lastAt, mtime(file));
    const open = s.turnOpen ?? r.turn?.status === 'inProgress';
    const state = !open ? 'done' : s.pendingInput ? 'waiting' : now - last > QUIET ? 'quiet' : 'busy';
    const role = String(r.agent_path ?? '').split('/').filter(Boolean).at(-1);
    if (!by.has(top)) by.set(top, []);
    by.get(top).push(subagent(s, last, state, role || r.agent_nickname || 'agent', r.agent_nickname ?? '', now));
  }
  return by;
}

/** Sorts a session's subagents, running ones first. A done session whose subagents still run isn't done: it resumes when they report. */
function withSubagents(state, subagents) {
  subagents.sort((a, b) => (a.state === 'done') - (b.state === 'done') || Date.parse(b.lastActivity) - Date.parse(a.lastActivity));
  if (state !== 'done') return state;
  return subagents.some(a => a.state === 'busy') ? 'busy' : subagents.some(a => a.state !== 'done') ? 'quiet' : 'done';
}

async function refresh() {
  const now = Date.now();
  const procs = await processes();
  const codexRunning = procs ? [...procs.values()].some(n => /^codex/i.test(n)) : null;
  const sessions = [];
  const keys = new Set();
  try { bridge.read(); } catch (e) { console.error('bridge:', e.message); }
  const claude = claudeSessions(procs);
  const codex = codexThreads(now);
  const codexSubs = codexSubagents(codex.subagents, now, keys);

  for (const j of claude) {
    try {
      const file = claudeTranscript(j);
      const key = `claude:${j.pid}`;
      keys.add(key);
      const s = file ? follow(key, file, claudeLine, now) : new Stats();
      const last = Math.max(s.lastAt, file ? mtime(file) : 0, j.statusUpdatedAt || 0);
      const subagents = file ? claudeSubagents(file, j.sessionId, now, keys) : [];
      const state = withSubagents(j.status === 'waiting' ? 'waiting'
        : j.status === 'busy' ? (now - last > QUIET ? 'quiet' : 'busy')
        : 'done', subagents);
      if (state === 'done' && now - last > DONE_KEEP) continue; // still followed, so it isn't read from the start again
      sessions.push({
        tool: 'claude',
        id: j.sessionId,
        link: j.hostSessionId ?? null,
        pid: j.pid,
        title: j.name || project(j.cwd),
        project: project(j.cwd),
        cwd: j.cwd,
        state,
        stateSince: iso(j.statusUpdatedAt),
        summary: [state === 'waiting' && j.waitingFor, s.text].filter(Boolean).join(' · '),
        lastActivity: iso(last),
        ...metrics(s, now),
        subagents,
        bridge: Bridge.summary(bridge.sessions.get(j.sessionId), now),
      });
    } catch (e) {
      console.error(`claude ${j.pid}:`, e.message);
    }
  }

  for (const r of codex.threads) {
    try {
      const file = stripLong(r.rollout_path);
      const key = `codex:${r.id}`;
      keys.add(key);
      const s = follow(key, file, codexLine, now);
      const last = Math.max(s.lastAt, mtime(file));
      const open = s.turnOpen ?? r.turn?.status === 'inProgress';
      const subagents = codexSubs.get(r.id) ?? [];
      const state = withSubagents(!open ? 'done'
        : s.pendingInput ? 'waiting'
        : codexRunning === false || now - last > ORPHAN ? 'dead'
        : now - last > QUIET ? 'quiet'
        : 'busy', subagents);
      if (state === 'done' && now - last > DONE_KEEP) continue;
      const since = open ? r.turn?.started_at : r.turn?.completed_at;
      const note = !open && ['failed', 'interrupted'].includes(r.turn?.status) ? r.turn.status : null;
      sessions.push({
        tool: 'codex',
        id: r.id,
        link: null,
        pid: null,
        title: r.name || r.title.split('\n')[0].slice(0, 80),
        project: project(r.cwd),
        cwd: stripLong(r.cwd),
        state,
        stateSince: iso(since ? since * 1000 : null),
        summary: [note, s.text].filter(Boolean).join(' · '),
        lastActivity: iso(last),
        ...metrics(s, now),
        tokensTotal: r.tokens_used,
        subagents,
        bridge: Bridge.summary(bridge.sessions.get(r.id), now),
      });
    } catch (e) {
      console.error(`codex ${r.id}:`, e.message);
    }
  }

  for (const key of followed.keys()) if (!keys.has(key)) followed.delete(key);

  const rank = s => (s.circles ? 0.5 : STATES.indexOf(s.state));
  sessions.sort((a, b) => rank(a) - rank(b) || Date.parse(b.lastActivity ?? 0) - Date.parse(a.lastActivity ?? 0));
  snapshot = {
    updated: iso(now),
    sessions,
    projects: bridge.projects(projectResolver([...claude.map(j => j.cwd), ...codex.cwds]), now),
  };
}

// ---------- replay ----------

function replay(file) {
  const s = new Stats();
  let parse = null;
  new Tail(file).read(line => {
    parse ??= line.includes('"session_meta"') ? codexLine : claudeLine;
    parse(s, line);
  });
  const times = [...s.tools, ...s.tokens].map(e => e.t);
  if (!times.length) { console.error('No tool calls or token counts found in', file); process.exit(1); }
  const first = times.reduce((a, b) => Math.min(a, b)), last = times.reduce((a, b) => Math.max(a, b));
  const start = new Date(first); start.setMinutes(0, 0, 0);
  const STEP = 5 * MIN; // the live watcher slides its hour every 30 s; replay samples it every 5 minutes

  const pad = (v, n) => String(v).padStart(n);
  const pct = x => (x === null ? '-' : `${Math.round(x * 100)}%`);
  const two = n => String(n).padStart(2, '0');
  console.log(`${parse === codexLine ? 'Codex rollout' : 'Claude transcript'}: ${file}`);
  console.log(`${new Date(first).toLocaleString()} -> ${new Date(last).toLocaleString()} (${((last - first) / HOUR).toFixed(1)} h)\n`);
  console.log('hour          tools/h  tokens/h  errors  repeat  new files  min since patch  circles shown');
  let tokens = 0, tools = 0, active = 0, idle = 0, litMin = 0, litHours = 0;
  for (let h = start.getTime(); h <= last; h += HOUR) {
    const m = metrics(s, h + HOUR);
    if (!m.toolsPerHour && !m.tokensPerHour) { idle++; continue; }
    if (idle) { console.log(`  (${idle} idle hour${idle > 1 ? 's' : ''})`); idle = 0; }
    let lit = 0;
    for (let now = h + STEP; now <= h + HOUR; now += STEP) if (metrics(s, now).circles) lit += STEP / MIN;
    active++;
    tokens += m.tokensPerHour;
    tools += m.toolsPerHour;
    litMin += lit;
    if (lit) litHours++;
    const d = new Date(h);
    const label = `${two(d.getMonth() + 1)}-${two(d.getDate())} ${two(d.getHours())}:00`;
    console.log(`${label.padEnd(12)} ${pad(m.toolsPerHour, 8)} ${pad((m.tokensPerHour / 1e6).toFixed(1) + 'M', 9)} ${pad(pct(m.errorRate), 7)} ${pad(pct(m.repeatRatio), 7)} ${pad(m.newFiles, 10)} ${pad(m.minsSincePatch ?? '-', 16)}  ${lit ? `CIRCLES ${lit} min` : ''}`);
  }
  console.log(`\n${active} active hours, ${tools} tool calls, ${(tokens / 1e6).toFixed(1)}M tokens.`);
  console.log(`Circles flag shown for ${litMin} min in ${litHours} hour${litHours === 1 ? '' : 's'}`
    + ` (>= ${CIRCLES.toolsPerHour} tool calls in the past hour, >= ${CIRCLES.repeatRatio * 100}% of them repeats, no new file patched).`);
  console.log('Rows show the hour itself; "circles shown" samples the live sliding hour every 5 minutes within it.');
}

// ---------- http ----------

const PAGE = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Agentwachter</title>
<style>
:root{--bg:#1c1d22;--row:#24252b;--line:#3a3c45;--text:#ececf0;--muted:#8b8e9a;--ok:#4cc38a;--warn:#f0a43a;--hot:#ef5b52}
body{margin:0;padding:16px;background:var(--bg);color:var(--text);font:13px/1.4 "Segoe UI",system-ui,sans-serif}
h1{font-size:15px;margin:0 0 12px;font-weight:600}h1 span{color:var(--muted);font-weight:400}
table{border-collapse:collapse;width:100%}td,th{padding:5px 8px;text-align:left;white-space:nowrap}
th{color:var(--muted);font-weight:400;font-size:12px}tr+tr td{border-top:1px solid var(--row)}tr.sub td{border-top:none}
td.sum{color:var(--muted);max-width:520px;overflow:hidden;text-overflow:ellipsis}td.n{text-align:right;font-family:Consolas,monospace}
td.tool{width:16px;position:relative}td.tool img{display:block;width:16px;height:16px}
td.tree::before{content:"";position:absolute;left:15px;top:0;bottom:0;border-left:1px solid var(--line)}
td.tree.end::before{bottom:50%}td.tree::after{content:"";position:absolute;left:15px;top:50%;width:11px;border-top:1px solid var(--line)}
tr.sub td.name{color:var(--muted)}.more{color:var(--muted)}
.chip{display:inline-block;padding:1px 8px;border-radius:9px;font-size:12px;color:#111}
.busy{background:var(--ok)}.waiting{background:var(--warn)}.quiet,.done{background:var(--muted)}.dead{background:var(--hot)}
.flag{color:var(--hot);font-weight:600}
</style></head><body>
<h1>Agentwachter <span id="upd"></span></h1>
<table><thead><tr><th></th><th>state</th><th>project</th><th>session</th><th>latest</th><th>tok/h</th><th>tools/h</th><th>errors</th><th>repeats</th><th>patch</th></tr></thead><tbody id="rows"></tbody></table>
<script>
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const pct = x => x == null ? '' : Math.round(x * 100) + '%';
const tok = n => n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : n || '';
const row = (a, cls, first, name, title) => '<tr class="' + cls + '">' + first
  + '<td><span class="chip ' + a.state + '">' + esc(a.state) + '</span>'
  + (a.circles ? ' <span class="flag" title="busy, mostly repeats, no new files">circles</span>' : '') + '</td>'
  + '<td class="name">' + esc(name) + '</td><td>' + esc(title) + '</td><td class="sum" title="' + esc(a.summary) + '">' + esc(a.summary) + '</td>'
  + '<td class="n">' + tok(a.tokensPerHour) + '</td><td class="n">' + (a.toolsPerHour || '') + '</td><td class="n">' + pct(a.errorRate) + '</td>'
  + '<td class="n">' + pct(a.repeatRatio) + '</td><td class="n">' + (a.minsSincePatch == null ? '' : a.minsSincePatch + 'm') + '</td></tr>';
// A session, then its running subagents on a tree line, then how many of them are done.
function rows(s) {
  const subs = s.subagents ?? [];
  const running = subs.filter(a => a.state !== 'done');
  const done = subs.length - running.length;
  let html = row(s, '', '<td class="tool"><img src="icons/' + s.tool + '.png" alt="' + s.tool + '" title="' + s.tool + '"></td>', s.project, s.title);
  if (!running.length) return html;
  html += running.map((a, i) => row(a, 'sub', '<td class="tool tree' + (i === running.length - 1 && !done ? ' end' : '') + '"></td>', a.name, a.title)).join('');
  if (done) html += '<tr class="sub"><td class="tool tree end"></td><td class="more" colspan="9">+' + done + ' done</td></tr>';
  return html;
}
async function load() {
  try {
    const d = await (await fetch('agents')).json();
    document.getElementById('upd').textContent = new Date(d.updated).toLocaleTimeString();
    document.getElementById('rows').innerHTML = d.sessions.map(rows).join('');
  } catch { document.getElementById('upd').textContent = 'not reachable'; }
}
load(); setInterval(load, 30000);
</script></body></html>`;

function serve() {
  const server = http.createServer((req, res) => {
    const url = req.url.split('?')[0];
    if (url === '/agents') {
      // 503 until the first refresh is done, so a client never mistakes "starting" for "no sessions".
      res.writeHead(snapshot.updated ? 200 : 503, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
      res.end(JSON.stringify(snapshot, null, 1));
    } else if (url === '/') {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      res.end(PAGE);
    } else if (/^\/icons\/(claude|codex)\.png$/.test(url)) {
      fs.readFile(path.join(import.meta.dirname, url), (err, png) => {
        if (err) return res.writeHead(404).end();
        res.writeHead(200, { 'content-type': 'image/png', 'cache-control': 'max-age=86400' });
        res.end(png);
      });
    } else {
      res.writeHead(404).end();
    }
  });
  server.on('error', e => {
    console.error(`Cannot listen on 127.0.0.1:${PORT}: ${e.code ?? e.message}`);
    if (e.code === 'EADDRINUSE') console.error('Another program, or a reserved port block (WSL, Hyper-V), holds it. Set AGENTWACHTER_PORT to another port.');
    process.exit(1);
  });
  server.listen(PORT, '127.0.0.1', () => console.log(`Agentwachter on http://127.0.0.1:${PORT}/ (JSON at /agents)`));
}

async function loop() {
  try { await refresh(); } catch (e) { console.error('refresh:', e); }
  setTimeout(loop, REFRESH);
}

export { Stats, Tail, metrics, claudeLine, codexLine };

const args = process.argv.slice(2);
if (!import.meta.main) {
  // imported (tests, analysis): no side effects
} else if (args[0] === '--replay') {
  if (!args[1]) { console.error('usage: node agentwachter.mjs --replay <rollout.jsonl | transcript.jsonl>'); process.exit(2); }
  replay(stripLong(args[1]));
} else if (args[0] === '--once') {
  await refresh();
  console.log(JSON.stringify(snapshot, null, 1));
} else if (args[0] === '--demo') {
  // A fixed snapshot instead of live sessions: for screenshots, or for working on the page and the tray.
  snapshot = readJson(stripLong(args[1] ?? ''));
  if (!snapshot?.updated) { console.error('usage: node agentwachter.mjs --demo <snapshot.json>'); process.exit(2); }
  serve();
} else {
  serve();
  loop();
}
