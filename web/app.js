// Explorer Connect, the web app. Served by Explorer Native over Tailscale only.
// Plain JavaScript, no libraries, no build step. VoiceOver is the main user:
// real buttons, a heading per screen, focus moved on purpose, and every change
// that matters said through the live region.

const $ = (id) => document.getElementById(id);

// ---------- Storage ----------

const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem('ec.' + key); return v == null ? fallback : JSON.parse(v); }
    catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem('ec.' + key, JSON.stringify(value)); } catch { /* private mode */ }
  },
  remove(key) { try { localStorage.removeItem('ec.' + key); } catch { } },
};

const DEFAULTS = {
  sort: 'name', foldersFirst: true, extensions: true, confirmDelete: true, conflict: 'rename',
  announceTransfers: true, skipBack: 15, skipFwd: 30, speed: 1, resume: true, playRest: true, continue: true,
};
const prefs = Object.assign({}, DEFAULTS, store.get('prefs', {}));
const savePrefs = () => store.set('prefs', prefs);

const state = {
  code: store.get('code', ''),
  trusted: false,
  user: null,
  computer: '',
  path: null,          // null is the drive list
  entries: [],
  selectMode: false,
  selected: new Set(),
  clip: null,          // { paths, cut } for Paste here
  native: new Set(['.mp3', '.m4a', '.m4b', '.m4r', '.aac', '.flac', '.wav', '.aif', '.aiff', '.caf', '.alac']),
  audio: new Set(['.mp3', '.flac', '.m4a', '.m4b', '.m4r', '.aac', '.wav', '.wma', '.aif', '.aiff', '.alac',
    '.ogg', '.oga', '.opus', '.mka', '.mp4', '.mov', '.mkv', '.avi', '.cda']),
};

// ---------- Speaking ----------

let liveTimer = 0;
function announce(message, urgent = false) {
  const region = urgent ? $('alert') : $('live');
  region.textContent = '';
  clearTimeout(liveTimer);
  liveTimer = setTimeout(() => { region.textContent = message; }, 60);
}

// ---------- Words ----------

const UNITS = [['byte', 'bytes'], ['kilobyte', 'kilobytes'], ['megabyte', 'megabytes'],
  ['gigabyte', 'gigabytes'], ['terabyte', 'terabytes'], ['petabyte', 'petabytes']];
function words(bytes) {
  if (bytes == null || bytes < 0) return '';
  let size = bytes, unit = 0;
  while (size >= 1024 && unit < UNITS.length - 1) { size /= 1024; unit++; }
  let rounded = unit === 0 ? Math.round(size) : Math.round(size * 10) / 10;
  if (rounded >= 1024 && unit < UNITS.length - 1) { rounded = Math.round(rounded / 1024 * 10) / 10; unit++; }
  const text = Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1);
  return `${text} ${rounded === 1 ? UNITS[unit][0] : UNITS[unit][1]}`;
}
function plural(n, one, many) { return `${n} ${n === 1 ? one : many}`; }
function dateText(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return '';
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });
}
function clock(seconds) {
  if (!isFinite(seconds) || seconds < 0) seconds = 0;
  const s = Math.floor(seconds % 60), m = Math.floor(seconds / 60) % 60, h = Math.floor(seconds / 3600);
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}
function spokenTime(seconds) {
  if (!isFinite(seconds) || seconds < 0) seconds = 0;
  const h = Math.floor(seconds / 3600), m = Math.floor(seconds / 60) % 60, s = Math.floor(seconds % 60);
  const parts = [];
  if (h) parts.push(plural(h, 'hour', 'hours'));
  if (m) parts.push(plural(m, 'minute', 'minutes'));
  if (!h && (s || !m)) parts.push(plural(s, 'second', 'seconds'));
  return parts.join(' ');
}
function extOf(name) { const i = name.lastIndexOf('.'); return i > 0 ? name.slice(i).toLowerCase() : ''; }
function leaf(path) {
  const t = path.replace(/[\\]+$/, '');
  const i = t.lastIndexOf('\\');
  return i >= 0 ? t.slice(i + 1) || t : t;
}
function parentOf(path) {
  const t = path.replace(/[\\]+$/, '');
  const i = t.lastIndexOf('\\');
  if (i < 0) return null;
  const p = t.slice(0, i);
  return /^[A-Za-z]:$/.test(p) ? p + '\\' : p;
}
function join(folder, name) { return folder.endsWith('\\') ? folder + name : folder + '\\' + name; }
function shownName(name, folder) {
  if (folder || prefs.extensions) return name;
  const e = extOf(name);
  return e ? name.slice(0, -e.length) : name;
}

// ---------- The PC ----------

class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

const UNREACHABLE = 'The PC could not be reached. Check that Tailscale is on, on this device and on the PC, ' +
  'and that Explorer Native is running.';

// fetch with a time limit, so nothing waits for ever on a PC that has gone away.
async function timedFetch(url, options = {}, ms = 20000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), ms);
  try {
    return await fetch(url, { ...options, signal: ctrl.signal, cache: 'no-store' });
  } finally {
    clearTimeout(timer);
  }
}

async function api(path, { method = 'GET', body, raw = false, timeout = 20000 } = {}) {
  const headers = {};
  if (state.code) headers['X-Connect-Code'] = state.code;
  let payload = body;
  if (body !== undefined && !raw) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  let res;
  try {
    res = await timedFetch(path, { method, headers, body: payload }, timeout);
  } catch (e) {
    throw new ApiError(0, e && e.name === 'AbortError' ? 'The PC took too long to answer. ' + UNREACHABLE : UNREACHABLE);
  }
  const type = res.headers.get('content-type') || '';
  const data = type.includes('json') ? await res.json().catch(() => null) : await res.text().catch(() => '');
  if (res.status === 401 && !state.trusted) {
    showCodeScreen('That pairing code is not right any more. Enter the one shown on the PC.');
  }
  if ((res.status === 502 || res.status === 503) && !(data && data.error)) {
    // Tailscale is up but nothing answers behind it: Explorer Native is closed.
    throw new ApiError(0, 'The PC answered, but Explorer Native is not running there. Start it, then try again.');
  }
  if (!res.ok) {
    const err = new ApiError(res.status, (data && data.error) || `The PC answered ${res.status}.`);
    err.data = data;
    throw err;
  }
  return data;
}

function mediaUrl(endpoint, path) {
  let url = `${endpoint}?path=${encodeURIComponent(path)}`;
  if (state.code && !state.trusted) url += `&code=${encodeURIComponent(state.code)}`;
  return url;
}
function codeQuery(url) {
  return state.code && !state.trusted ? url + (url.includes('?') ? '&' : '?') + 'code=' + encodeURIComponent(state.code) : url;
}

// ---------- Screens ----------

const SCREENS = ['files', 'player', 'transfers', 'clipboard', 'settings'];
let currentTab = 'files';
const tabScroll = {};
const TITLES = { files: 'files-title', player: 'player-title', transfers: 'transfers-title',
  clipboard: 'clip-title', settings: 'settings-title' };

// The top bar shows the screen's title; the screen's own heading carries it for VoiceOver.
function setBarTitle(text) { setText($('bar-title'), text); }

// The back button names where it goes, as iOS does: the folder above, or Drives.
function setBackLabel() {
  const show = currentTab === 'files' && state.path != null && $('screen-code').hidden && $('screen-offline').hidden;
  const up = $('btn-up');
  if (up.hidden === show) up.hidden = !show;
  if (!show) return;
  const parent = parentOf(state.path);
  const to = parent == null ? 'Drives' : (leaf(parent) || parent.replace(/\\$/, ''));
  setText($('back-label'), to);
  up.setAttribute('aria-label', `Back to ${to}`);
}

// The small title in the bar appears once the large one has scrolled away.
function onMainScroll() {
  const on = $('main').scrollTop > 30;
  if (document.body.classList.contains('scrolled') !== on) document.body.classList.toggle('scrolled', on);
}

function showTab(name, focus = true) {
  // Each tab keeps its place: where it was scrolled to when it was left.
  if (currentTab && $('screen-' + currentTab) && !$('screen-' + currentTab).hidden) tabScroll[currentTab] = $('main').scrollTop;
  currentTab = name;
  $('screen-code').hidden = true;
  $('screen-offline').hidden = true;
  $('tabs').hidden = false;
  for (const s of SCREENS) $('screen-' + s).hidden = s !== name;
  for (const b of document.querySelectorAll('#tabs button')) {
    const on = b.dataset.tab === name;
    b.setAttribute('aria-selected', on ? 'true' : 'false');
    b.tabIndex = on ? 0 : -1;
  }
  if (name === 'transfers') renderTransfers();
  if (name === 'settings') renderSettings();
  if (name === 'player') renderPlayer();
  if (name === 'clipboard') getClipboard();
  pollJobs();
  setBarTitle($(TITLES[name]).textContent);
  setBackLabel();
  $('main').scrollTop = tabScroll[name] || 0;
  onMainScroll();
  // VoiceOver lands on the new screen's heading, once.
  if (focus) $(TITLES[name]).focus({ preventScroll: true });
}

function showCodeScreen(message) {
  setBarTitle('Pairing code');
  $('btn-up').hidden = true;
  for (const s of SCREENS) $('screen-' + s).hidden = true;
  $('tabs').hidden = true;
  $('screen-offline').hidden = true;
  $('screen-code').hidden = false;
  $('code-error').textContent = message || '';
  $('code-title').focus();
}

// The PC cannot be reached at all: one plain sentence and a Retry button.
function showOffline(message) {
  setBarTitle("Can't reach the PC");
  $('btn-up').hidden = true;
  for (const s of SCREENS) $('screen-' + s).hidden = true;
  $('tabs').hidden = true;
  $('screen-code').hidden = true;
  $('screen-offline').hidden = false;
  $('offline-text').textContent = message || UNREACHABLE;
  $('offline-title').focus();
  announce(message || UNREACHABLE, true);
}

// ---------- Dialogs ----------

function dialog({ title, body, buttons, focus }) {
  const d = $('dialog');
  $('dialog-title').textContent = title;
  const holder = $('dialog-body');
  holder.replaceChildren();
  if (typeof body === 'string') { const p = document.createElement('p'); p.textContent = body; holder.append(p); }
  else if (body) holder.append(body);
  const bar = $('dialog-buttons');
  bar.replaceChildren();
  for (const b of buttons) {
    const btn = document.createElement('button');
    btn.type = b.type || 'submit';
    btn.value = b.value;
    btn.textContent = b.label;
    if (b.primary) btn.className = 'primary';
    if (b.onClick) btn.addEventListener('click', (e) => { e.preventDefault(); b.onClick(btn); });
    bar.append(btn);
  }
  return new Promise((resolve) => {
    const done = () => { d.removeEventListener('close', done); resolve(d.returnValue); };
    d.returnValue = '';
    d.addEventListener('close', done);
    d.showModal();
    const target = focus ? holder.querySelector(focus) : bar.querySelector('button');
    if (target) target.focus();
  });
}

async function ask(title, label, value = '') {
  const wrap = document.createElement('div');
  const l = document.createElement('label');
  l.textContent = label;
  l.htmlFor = 'dialog-input';
  const input = document.createElement('input');
  input.id = 'dialog-input';
  input.value = value;
  input.autocomplete = 'off';
  input.setAttribute('autocapitalize', 'off');
  wrap.append(l, input);
  const answer = await dialog({ title, body: wrap, buttons: [
    { label: 'OK', value: 'ok', primary: true }, { label: 'Cancel', value: 'cancel' }], focus: '#dialog-input' });
  return answer === 'ok' ? input.value.trim() : null;
}

async function confirmBox(title, text, yes) {
  const answer = await dialog({ title, body: text, buttons: [
    { label: yes, value: 'yes', primary: true }, { label: 'Cancel', value: 'cancel' }] });
  return answer === 'yes';
}

// ---------- Files ----------

async function openFolder(path, { push = true, focusName = null, quiet = false } = {}) {
  try {
    let entries;
    if (path == null) {
      const drives = await api('/api/drives');
      entries = drives.map((d) => ({
        name: d.name, folder: true, drive: true, size: d.size, used: d.used, label: d.label,
        kind: d.kind, unlimited: d.unlimited,
      }));
    } else {
      entries = await api(mediaUrl('/api/list', path).replace(/&code=[^&]*/, ''));
    }
    state.path = path;
    state.entries = entries;
    state.selected.clear();
    if (path != null) store.set('lastPath', path);
    if (push) history.pushState({ path }, '');
    renderFiles();
    const title = path == null ? 'Drives' : leaf(path);
    $('files-title').textContent = title;
    if (currentTab === 'files') setBarTitle(title);
    $('files-path').textContent = path == null ? state.computer : path;
    document.title = `${title} - Explorer Connect`;
    if (!quiet) {
      if (focusName) {
        const row = [...$('file-list').querySelectorAll('li')].find((li) => li.dataset.name === focusName);
        if (row) { row.querySelector('.main').focus(); return; }
      }
      $('files-title').focus();
      announce(plural(entries.length, 'item', 'items'));
    }
  } catch (e) {
    if (e.status === 401) return;
    if (e.status === 0 && state.entries.length === 0) { showOffline(e.message); return; }
    announce(e.message, true);
    if (path != null && state.path == null && !state.entries.length) openFolder(null, { push: false });
  }
}

function sorted(entries) {
  const list = entries.slice();
  const byName = (a, b) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
  const cmp = {
    name: byName,
    date: (a, b) => (new Date(b.modified) - new Date(a.modified)) || byName(a, b),
    size: (a, b) => ((b.size || 0) - (a.size || 0)) || byName(a, b),
    type: (a, b) => extOf(a.name).localeCompare(extOf(b.name)) || byName(a, b),
  }[prefs.sort] || byName;
  list.sort((a, b) => (prefs.foldersFirst && a.folder !== b.folder ? (a.folder ? -1 : 1) : 0) || cmp(a, b));
  return list;
}

function entryMeta(e) {
  if (e.drive) {
    const label = e.label ? `${e.label}, ` : '';
    if (e.unlimited) return `${label}${words(e.used)} used`;
    return e.size > 0 ? `${label}${words(e.used)} of ${words(e.size)} used` : label.replace(/, $/, '');
  }
  if (e.folder) return ['folder', e.sizeText, dateText(e.modified)].filter(Boolean).join(', ');
  return [words(e.size), dateText(e.modified)].filter(Boolean).join(', ');
}

function renderFiles() {
  const list = $('file-list');
  const frag = document.createDocumentFragment();
  const entries = state.path == null ? state.entries : sorted(state.entries);
  if (state.path != null) state.entries = entries;
  entries.forEach((e) => {
    const li = document.createElement('li');
    li.dataset.name = e.name;
    if (e.folder || e.drive) li.className = 'folder';
    const full = e.drive ? e.name : join(state.path, e.name);
    if (state.selectMode && !e.drive) {
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = state.selected.has(full);
      box.setAttribute('aria-label', `Select ${shownName(e.name, e.folder)}`);
      box.addEventListener('change', () => {
        if (box.checked) state.selected.add(full); else state.selected.delete(full);
        updateSelectCount();
      });
      li.append(box);
    }
    const main = document.createElement('button');
    main.type = 'button';
    main.className = 'main';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = e.drive ? e.name.replace(/\\$/, '') : shownName(e.name, e.folder);
    const meta = document.createElement('span');
    meta.className = 'meta';
    meta.textContent = entryMeta(e);
    main.append(name, meta);
    main.addEventListener('click', () => activate(e, full));
    li.append(main);
    if (!e.drive) {
      const more = document.createElement('button');
      more.type = 'button';
      more.className = 'side';
      more.textContent = 'More';
      more.setAttribute('aria-label', `More for ${shownName(e.name, e.folder)}`);
      more.addEventListener('click', () => actions(e, full, more));
      li.append(more);
    }
    frag.append(li);
  });
  list.replaceChildren(frag);
  $('files-empty').hidden = entries.length > 0;
  setBackLabel();
  for (const id of ['btn-play-folder', 'btn-new-folder', 'btn-upload', 'btn-select'])
    $(id).hidden = state.path == null;
  $('btn-paste').hidden = !(state.clip && state.path != null);
  if (state.clip) $('btn-paste').textContent = state.clip.cut
    ? `Move ${plural(state.clip.paths.length, 'item', 'items')} here`
    : `Paste ${plural(state.clip.paths.length, 'item', 'items')} here`;
  $('files-toolbar').hidden = state.selectMode;
  $('select-toolbar').hidden = !state.selectMode;
  updateSelectCount();
}

function updateSelectCount() {
  $('select-count').textContent = `${state.selected.size} selected`;
}

function isAudio(name) { return state.audio.has(extOf(name)); }

function activate(e, full) {
  if (e.folder) { openFolder(full); return; }
  if (isAudio(e.name)) {
    if (prefs.playRest) {
      const tracks = state.entries.filter((x) => !x.folder && isAudio(x.name));
      const start = tracks.findIndex((x) => x.name === e.name);
      playQueue(tracks.map((x) => ({ path: join(state.path, x.name), name: x.name })), Math.max(0, start));
    } else {
      playQueue([{ path: full, name: e.name }], 0);
    }
    return;
  }
  window.open(mediaUrl('/api/file', full), '_blank', 'noopener');
}

async function actions(e, full, opener) {
  const items = [];
  if (e.folder) items.push(['open', 'Open']); else if (isAudio(e.name)) items.push(['open', 'Play']); else items.push(['open', 'Open']);
  items.push(['details', 'Details'], ['rename', 'Rename'], ['delete', 'Delete'], ['copy', 'Copy'], ['move', 'Move'],
    ['pc', 'Copy on PC']);
  if (!e.folder) items.push(['save', `Save to ${DEVICE}`]);
  if (e.folder) items.push(['size', 'Get size']);
  items.push(['cancel', 'Cancel']);
  const choice = await dialog({ title: shownName(e.name, e.folder), body: null,
    buttons: items.map(([value, label]) => ({ value, label })) });
  switch (choice) {
    case 'open': activate(e, full); break;
    case 'details': await details(full); break;
    case 'rename': await rename(e, full); break;
    case 'delete': await remove([full]); break;
    case 'copy': hold([full], false); break;
    case 'move': hold([full], true); break;
    case 'pc': await copyOnPc([full]); break;
    case 'save': saveToPhone(full, e.name); break;
    case 'size': await folderSize(e, full); break;
    default: break;
  }
  if (opener && document.body.contains(opener) && !['open', 'rename', 'delete'].includes(choice)) opener.focus();
}

async function rename(e, full) {
  const name = await ask('Rename', 'New name', e.name);
  if (!name || name === e.name) return;
  try {
    await api('/api/rename', { method: 'POST', body: { path: full, newName: name } });
    announce(`Renamed to ${name}`);
    await openFolder(state.path, { push: false, focusName: name });
  } catch (err) { announce(err.message, true); }
}

async function remove(paths) {
  const what = paths.length === 1 ? shownName(leaf(paths[0]), false) : plural(paths.length, 'item', 'items');
  if (prefs.confirmDelete && !(await confirmBox('Delete', `Delete ${what}? Files on the PC go to the Recycle Bin, and Google Drive files go to the Drive trash.`, 'Delete'))) return;
  try {
    const r = await api('/api/delete', { method: 'POST', body: { paths } });
    const failed = (r.failed || []).length;
    announce(failed ? `Deleted ${r.deleted}. ${plural(failed, 'item', 'items')} could not be deleted: ${r.failed[0].error}` : `Deleted ${what}`, failed > 0);
    state.selectMode = false;
    await openFolder(state.path, { push: false, quiet: true });
    $('files-title').focus();
  } catch (err) { announce(err.message, true); }
}

function hold(paths, cut) {
  state.clip = { paths, cut };
  announce(`${cut ? 'Ready to move' : 'Copied'} ${paths.length === 1 ? leaf(paths[0]) : plural(paths.length, 'item', 'items')}. Go to a folder and choose ${cut ? 'Move' : 'Paste'} here.`);
  renderFiles();
}

async function paste() {
  if (!state.clip || state.path == null) return;
  const { paths, cut } = state.clip;
  try {
    const r = await api(cut ? '/api/move' : '/api/copy', { method: 'POST',
      body: { paths, destination: state.path, conflict: prefs.conflict } });
    trackJob(r.job, `${cut ? 'Moving' : 'Copying'} ${paths.length === 1 ? leaf(paths[0]) : plural(paths.length, 'item', 'items')}`, state.path);
    if (cut) state.clip = null;
    renderFiles();
    announce(`${cut ? 'Moving' : 'Copying'}. Progress is on the Transfers tab.`);
  } catch (err) { announce(err.message, true); }
}

async function copyOnPc(paths) {
  try {
    await api('/api/clipboard/files', { method: 'POST', body: { paths } });
    announce('On the PC clipboard. Paste it on the PC with Control V.');
  } catch (err) { announce(err.message, true); }
}

function saveToPhone(full, name) {
  const a = document.createElement('a');
  a.href = mediaUrl('/api/file', full);
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  announce(`Saving ${name}. Safari asks where to put it.`);
}

async function folderSize(e, full) {
  announce('Measuring');
  try {
    const r = await api(mediaUrl('/api/size', full).replace(/&code=[^&]*/, ''));
    e.sizeText = `${words(r.bytes)}${r.complete ? '' : ' or more'}, ${plural(r.files, 'file', 'files')}`;
    announce(`${shownName(e.name, true)}: ${e.sizeText}`);
    renderFiles();
  } catch (err) { announce(err.message, true); }
}

async function details(full) {
  announce('Getting details');
  let r;
  try { r = await api(mediaUrl('/api/details', full).replace(/&code=[^&]*/, '')); }
  catch (err) { announce(err.message, true); return; }
  const wrap = document.createElement('div');
  wrap.className = 'scroll';
  const lines = [];
  const add = (dl, name, value) => {
    if (value == null || value === '' || (Array.isArray(value) && !value.length)) return;
    const dt = document.createElement('dt'); dt.textContent = name;
    const dd = document.createElement('dd'); dd.textContent = Array.isArray(value) ? value.join(', ') : String(value);
    dl.append(dt, dd);
    lines.push(`${name}: ${dd.textContent}`);
  };
  const section = (title, fill) => {
    const h = document.createElement('h3'); h.textContent = title;
    const dl = document.createElement('dl');
    fill(dl);
    if (dl.children.length) { wrap.append(h, dl); lines.push(''); }
  };
  const f = r.file || r;
  section('File', (dl) => {
    add(dl, 'Name', f.name); add(dl, 'Type', f.kind); add(dl, 'Size', f.folder ? null : words(f.size));
    add(dl, 'Size on disk', f.sizeOnDisk != null ? words(f.sizeOnDisk) : null);
    add(dl, 'Modified', f.modified ? new Date(f.modified).toLocaleString() : null);
    add(dl, 'Created', f.created ? new Date(f.created).toLocaleString() : null);
    add(dl, 'Attributes', f.attributes); add(dl, 'Owner', f.owner); add(dl, 'Path', r.path || full);
  });
  if (r.folder) section('Contents', (dl) => {
    add(dl, 'Items', r.folder.items); add(dl, 'Files', r.folder.files); add(dl, 'Folders', r.folder.folders);
  });
  if (r.media) section('Media', (dl) => {
    const m = r.media;
    add(dl, 'Length', m.durationSeconds ? spokenTime(m.durationSeconds) : null);
    add(dl, 'Container', m.container);
    add(dl, 'Bit rate', m.bitrate ? `${Math.round(m.bitrate / 1000)} kilobits a second` : null);
    for (const a of m.audio || []) {
      add(dl, 'Audio', [a.codec, a.codecProfile, a.sampleRate && `${a.sampleRate} hertz`,
        a.bitsPerSample && `${a.bitsPerSample} bit`, a.channels && plural(a.channels, 'channel', 'channels'),
        a.lossless ? 'lossless' : null].filter(Boolean).join(', '));
    }
    for (const t of m.tags || []) add(dl, t.name, t.value);
    for (const c of m.chapters || []) add(dl, `Chapter at ${clock(c.startSeconds)}`, c.title);
  });
  if (r.text) section('Text', (dl) => {
    add(dl, 'Encoding', r.text.encoding); add(dl, 'Lines', r.text.lines); add(dl, 'Words', r.text.words);
    add(dl, 'Line endings', r.text.lineEndings);
  });
  if (r.image) section('Image', (dl) => {
    add(dl, 'Dimensions', r.image.width && `${r.image.width} by ${r.image.height}`);
    add(dl, 'Camera', r.image.camera); add(dl, 'Taken', r.image.taken);
  });
  const choice = await dialog({ title: 'Details', body: wrap, buttons: [
    { label: 'Copy all details', value: 'copy' }, { label: 'Done', value: 'done', primary: true }] });
  if (choice === 'copy') {
    try { await navigator.clipboard.writeText(lines.join('\n').trim()); announce('Details copied'); }
    catch { announce('This device did not allow copying.', true); }
  }
}

async function newFolder() {
  const name = await ask('New folder', 'Folder name', 'New folder');
  if (!name) return;
  try {
    await api('/api/mkdir', { method: 'POST', body: { parent: state.path, name } });
    announce(`Made ${name}`);
    await openFolder(state.path, { push: false, focusName: name });
  } catch (err) { announce(err.message, true); }
}

// ---------- Transfers ----------

const transfers = [];
let transferSeq = 0;

function addTransfer(t) {
  t.key = ++transferSeq;
  t.started = Date.now();
  t.spoken = 0;
  transfers.unshift(t);
  renderTransfers();
  return t;
}

function percent(t) { return t.total > 0 ? Math.floor((t.done / t.total) * 100) : 0; }

function transferText(t) {
  const pct = percent(t);
  switch (t.state) {
    case 'done': return t.kind === 'download' ? `${t.name}, ${t.message || 'saved'}` : `${t.name}, done${t.message ? '. ' + t.message : ''}`;
    case 'failed': return `${t.name}, failed: ${t.message || 'unknown problem'}`;
    case 'cancelled': return `${t.name}, stopped`;
    case 'waiting': return `${t.name}, waiting for the PC, ${pct} percent`;
    default: return `${t.name}, ${pct} percent${t.total ? `, ${words(t.done)} of ${words(t.total)}` : ''}`;
  }
}

function transferProgress(t) {
  if (t.state !== 'running' || !prefs.announceTransfers) return;
  const step = Math.floor(percent(t) / 25);
  if (step > t.spoken && step < 4) { t.spoken = step; announce(transferText(t)); }
}

function finishTransfer(t, stateName, message) {
  t.state = stateName;
  t.message = message || t.message;
  announce(transferText(t), stateName === 'failed');
  renderTransfers();
  if (stateName === 'done' && t.folder && t.folder === state.path) openFolder(state.path, { push: false, quiet: true });
}

function renderTransfers() {
  if (currentTab !== 'transfers') return;
  const list = $('transfer-list');
  const frag = document.createDocumentFragment();
  for (const t of transfers) {
    const li = document.createElement('li');
    const main = document.createElement('div');
    main.className = 'main';
    const name = document.createElement('span'); name.className = 'name'; name.textContent = transferText(t);
    main.append(name);
    if (t.state === 'running' || t.state === 'waiting') {
      const bar = document.createElement('progress');
      bar.className = 'progress'; bar.max = 100; bar.value = percent(t);
      bar.setAttribute('aria-hidden', 'true');
      main.append(bar);
    }
    li.append(main);
    if ((t.state === 'running' || t.state === 'waiting') && t.cancel) {
      const stop = document.createElement('button');
      stop.type = 'button'; stop.textContent = 'Stop';
      stop.setAttribute('aria-label', `Stop ${t.name}`);
      stop.addEventListener('click', () => t.cancel());
      li.append(stop);
    }
    frag.append(li);
  }
  list.replaceChildren(frag);
  $('transfers-empty').hidden = transfers.length > 0;
}

// Jobs on the PC (copy, move, a finished upload going on to Drive).
function trackJob(id, name, folder) {
  const t = addTransfer({ kind: 'job', jobId: id, name, folder, state: 'running', done: 0, total: 0 });
  t.cancel = async () => {
    try { await api('/api/job/cancel', { method: 'POST', body: { id } }); } catch (err) { announce(err.message, true); }
  };
  pollJobs();
  return t;
}

let jobTimer = 0;
function pollJobs() {
  clearTimeout(jobTimer);
  if (document.hidden) return; // battery: nothing runs in the background
  const running = transfers.filter((t) => t.kind === 'job' && t.state === 'running');
  if (!running.length) return;
  jobTimer = setTimeout(async () => {
    for (const t of running) {
      try {
        const j = await api(`/api/job?id=${encodeURIComponent(t.jobId)}`);
        if (j.bytes > 0) { t.done = j.bytesDone; t.total = j.bytes; } else { t.done = j.itemsDone; t.total = j.items; }
        if (j.state === 'running') transferProgress(t);
        else {
          const failed = (j.failed || []).length;
          finishTransfer(t, j.state === 'done' ? 'done' : j.state === 'cancelled' ? 'cancelled' : 'failed',
            j.message || (failed ? `${plural(failed, 'item', 'items')} failed: ${j.failed[0].error}` : ''));
        }
      } catch (err) {
        if (err.status === 404) finishTransfer(t, 'failed', 'The PC forgot this job; it may have restarted.');
        else if (err.status === 0 && t.state !== 'waiting') { t.state = 'waiting'; renderTransfers(); }
      }
    }
    renderTransfers();
    pollJobs();
  }, 1000);
}

// Uploads, resumable: 4 MB chunks, picking up where the PC says it got to.
const CHUNK = 4 * 1024 * 1024;

// A file up to one chunk goes in a single request, tried again while the network comes back.
async function uploadSmall(file, folder) {
  const t = addTransfer({ kind: 'upload', name: file.name, folder, state: 'running', done: 0, total: file.size });
  let stopped = false;
  t.cancel = async () => { stopped = true; finishTransfer(t, 'cancelled'); };
  for (let attempt = 1; ; attempt++) {
    try {
      const url = `/api/upload?folder=${encodeURIComponent(folder)}&name=${encodeURIComponent(file.name)}` +
        `&conflict=${encodeURIComponent(prefs.conflict)}`;
      await api(url, { method: 'POST', body: file, raw: true, timeout: 120000 });
      if (!stopped) { t.done = t.total; finishTransfer(t, 'done'); }
      return;
    } catch (err) {
      if (stopped) return;
      if (err.status !== 0 || attempt >= 6) { finishTransfer(t, 'failed', err.message); return; }
      t.state = 'waiting';
      renderTransfers();
      await waitForNetwork(Math.min(30000, 2000 * attempt));
      t.state = 'running';
    }
  }
}

async function upload(file, folder) {
  if (file.size <= CHUNK) return uploadSmall(file, folder);
  const t = addTransfer({ kind: 'upload', name: file.name, folder, state: 'running', done: 0, total: file.size });
  let stopped = false, id = null;
  t.cancel = async () => {
    stopped = true;
    if (id) try { await api('/api/upload/cancel', { method: 'POST', body: { id } }); } catch { }
    finishTransfer(t, 'cancelled');
  };
  try {
    const s = await api('/api/upload/start', { method: 'POST',
      body: { folder, name: file.name, size: file.size, conflict: prefs.conflict } });
    id = s.id;
    let offset = 0, failures = 0;
    while (offset < file.size && !stopped) {
      try {
        const r = await api(`/api/upload/chunk?id=${encodeURIComponent(id)}&offset=${offset}`,
          { method: 'PUT', body: file.slice(offset, Math.min(file.size, offset + CHUNK)), raw: true, timeout: 120000 });
        offset = r.received;
        failures = 0;
        t.state = 'running';
      } catch (err) {
        if (stopped) return;
        if (err.status === 409 && err.data && typeof err.data.received === 'number') { offset = err.data.received; continue; }
        if (err.status && err.status !== 0 && err.status < 500) throw err;
        // The PC or the network went away: wait and ask how much it has, then carry on.
        t.state = 'waiting';
        renderTransfers();
        failures++;
        await waitForNetwork(Math.min(30000, 2000 * failures));
        try { offset = (await api(`/api/upload/status?id=${encodeURIComponent(id)}`)).received; } catch { }
      }
      t.done = offset;
      transferProgress(t);
      renderTransfers();
    }
    if (stopped) return;
    const f = await api('/api/upload/finish', { method: 'POST', body: { id } });
    if (f.job) { t.state = 'done'; transfers.splice(transfers.indexOf(t), 1); trackJob(f.job, `Sending ${file.name} to Google Drive`, folder); }
    else finishTransfer(t, 'done');
  } catch (err) {
    if (!stopped) finishTransfer(t, 'failed', err.message);
  }
}

function waitForNetwork(ms) {
  return new Promise((resolve) => {
    const go = () => { window.removeEventListener('online', go); document.removeEventListener('visibilitychange', vis); resolve(); };
    const vis = () => { if (!document.hidden && navigator.onLine) go(); };
    window.addEventListener('online', go);
    document.addEventListener('visibilitychange', vis);
    setTimeout(() => { if (navigator.onLine && !document.hidden) go(); }, ms);
  });
}

async function uploadFiles(files) {
  const folder = state.path;
  if (folder == null) return;
  announce(`Uploading ${plural(files.length, 'file', 'files')}`);
  for (const f of files) await upload(f, folder);
}

// ---------- Player ----------

const audio = () => $('audio');
const player = { queue: [], index: -1, repeat: 'off', shuffle: false, order: [], sleepTimer: 0, sleepAtEnd: false };
let lastSave = 0;

const SPEEDS = [0.5, 0.75, 0.8, 0.9, 1, 1.1, 1.2, 1.25, 1.3, 1.5, 1.75, 2, 2.5, 3];

function speedText(s) { return s === 1 ? 'Normal' : `${s} times`; }

function srcFor(path) {
  return state.native.has(extOf(path)) ? mediaUrl('/api/file', path) : mediaUrl('/api/audio', path);
}

function playQueue(queue, index) {
  player.queue = queue;
  player.order = queue.map((_, i) => i);
  if (player.shuffle) shuffleOrder(index);
  loadTrack(index, true);
  showTab('player', false);
  $('np-play').focus();
}

function shuffleOrder(first) {
  const rest = player.queue.map((_, i) => i).filter((i) => i !== first);
  for (let i = rest.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [rest[i], rest[j]] = [rest[j], rest[i]]; }
  player.order = [first, ...rest];
}

function loadTrack(index, autoplay, startAt) {
  if (index < 0 || index >= player.queue.length) return;
  player.index = index;
  const track = player.queue[index];
  const a = audio();
  a.src = srcFor(track.path);
  a.playbackRate = prefs.speed;
  a.defaultPlaybackRate = prefs.speed;
  a.preservesPitch = true;
  const resumeAt = startAt != null ? startAt : (prefs.resume ? store.get('pos:' + track.path, 0) : 0);
  a.dataset.resume = String(resumeAt || 0);
  store.set('session', { queue: player.queue, index, });
  if (autoplay) a.play().catch(() => announce('Press Play to start.'));
  renderPlayer();
  updatePosition();
  updateMetadata(track);
}

function current() { return player.queue[player.index]; }

function step(delta) {
  if (!player.queue.length) return;
  const pos = player.order.indexOf(player.index);
  let next = pos + delta;
  if (next >= player.order.length) { if (player.repeat === 'all') next = 0; else { announce('End of the queue'); return; } }
  if (next < 0) next = player.repeat === 'all' ? player.order.length - 1 : 0;
  loadTrack(player.order[next], true, 0);
}

function togglePlay() {
  const a = audio();
  if (!a.src) {
    const s = store.get('session', null);
    if (s && s.queue && s.queue.length) { player.queue = s.queue; player.order = s.queue.map((_, i) => i); loadTrack(s.index, true); }
    return;
  }
  if (a.paused) a.play().catch((e) => announce('Could not play: ' + e.message, true)); else a.pause();
}

function skip(seconds) {
  const a = audio();
  if (!a.src) return;
  a.currentTime = Math.max(0, Math.min((a.duration || 0) - 0.5, a.currentTime + seconds));
  announce(`${spokenTime(a.currentTime)}`);
}

function savePosition(force = false) {
  const t = current();
  const a = audio();
  if (!t || !a.src) return;
  const now = Date.now();
  if (!force && now - lastSave < 15000) return;
  lastSave = now;
  const nearEnd = a.duration && a.currentTime > a.duration - 10;
  if (nearEnd) store.remove('pos:' + t.path); else store.set('pos:' + t.path, Math.floor(a.currentTime));
}

// While something plays, nothing on the page may change on its own. Every
// DOM, ARIA, live-region or Media Session update below happens because of
// something the person did (play, pause, seek, skip, a track change) or a
// once-every-5-seconds refresh of the slider while VoiceOver is on it. On an
// iPhone, rewriting the slider's value every second while audio played was
// enough to stop VoiceOver talking.

// Changes text only when it is different, so nothing is rewritten for nothing.
function setText(el, text) { if (el.textContent !== text) el.textContent = text; }

let queueShown = '';

function renderPlayer() {
  const t = current();
  const a = audio();
  const playing = !!a.src && !a.paused;
  setText($('np-track'), t ? shownName(t.name, false) : 'Nothing is playing.');
  setText($('np-sub'), t ? `Track ${player.index + 1} of ${player.queue.length}, ${leaf(parentOf(t.path) || '')}` : '');
  setText($('np-play'), playing ? 'Pause' : 'Play');
  setText($('np-back'), `Back ${prefs.skipBack} seconds`);
  setText($('np-fwd'), `Forward ${prefs.skipFwd} seconds`);
  if ($('np-speed').value !== String(prefs.speed)) $('np-speed').value = String(prefs.speed);
  const resume = $('np-resume');
  const s = a.src ? null : store.get('session', null);
  const showResume = !a.src && !!(prefs.continue && s && s.queue && s.queue[s.index]);
  if (resume.hidden === showResume) resume.hidden = !showResume;
  if (showResume) {
    const tr = s.queue[s.index];
    const at = store.get('pos:' + tr.path, 0);
    setText(resume, `Resume ${shownName(tr.name, false)}${at ? ' at ' + spokenTime(at) : ''}`);
  }
  // The queue is rebuilt only when it or the track changes, never on play or pause.
  const key = player.queue.length + ':' + player.index + ':' + (player.queue[0] && player.queue[0].path);
  if (key !== queueShown) { queueShown = key; renderQueue(); }
  renderMini();
}

function renderQueue() {
  const list = $('np-queue');
  const frag = document.createDocumentFragment();
  player.queue.forEach((tr, i) => {
    const li = document.createElement('li');
    if (i === player.index) li.setAttribute('aria-current', 'true');
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'main';
    const n = document.createElement('span'); n.className = 'name';
    n.textContent = shownName(tr.name, false) + (i === player.index ? ', playing' : '');
    b.append(n);
    b.addEventListener('click', () => loadTrack(i, true, undefined));
    li.append(b);
    frag.append(li);
  });
  list.replaceChildren(frag);
}

// The mini player above the tab bar: the title and Play or Pause. No time.
function renderMini() {
  const t = current();
  const a = audio();
  const mini = $('mini');
  const show = !!t && !!a.src;
  if (mini.hidden === show) { mini.hidden = !show; document.body.classList.toggle('has-mini', show); }
  if (!show) return;
  setText($('mini-title'), shownName(t.name, false));
  $('mini-title').setAttribute('aria-label', `Now playing: ${shownName(t.name, false)}. Open Now Playing`);
  setText($('mini-play'), a.paused ? 'Play' : 'Pause');
}

// The slider and the time text: only when asked for, never on a tick.
function updatePosition() {
  const a = audio();
  const range = $('np-position');
  const dur = isFinite(a.duration) ? a.duration : 0;
  const now = a.currentTime || 0;
  const max = String(Math.floor(dur));
  if (range.max !== max) range.max = max;
  if (document.activeElement !== range) {
    const v = String(Math.floor(now));
    if (range.value !== v) range.value = v;
  }
  const text = `${spokenTime(now)} of ${spokenTime(dur)}`;
  if (range.getAttribute('aria-valuetext') !== text) range.setAttribute('aria-valuetext', text);
  setText($('np-time'), `${clock(now)} / ${clock(dur)}`);
}

// Lock screen and headphones: position only when it changes for a reason.
function syncSession() {
  if (!('mediaSession' in navigator)) return;
  const a = audio();
  try { navigator.mediaSession.playbackState = a.src ? (a.paused ? 'paused' : 'playing') : 'none'; } catch { }
  const dur = isFinite(a.duration) ? a.duration : 0;
  if (dur > 0 && navigator.mediaSession.setPositionState) {
    try { navigator.mediaSession.setPositionState({ duration: dur, playbackRate: a.playbackRate || 1, position: Math.min(a.currentTime || 0, dur) }); } catch { }
  }
}

function sayTime() {
  const a = audio();
  if (!a.src) { announce('Nothing is playing.'); return; }
  const dur = isFinite(a.duration) ? a.duration : 0;
  updatePosition();
  announce(dur > 0
    ? `${spokenTime(a.currentTime)} of ${spokenTime(dur)}, ${spokenTime(Math.max(0, dur - a.currentTime))} left`
    : spokenTime(a.currentTime));
}

async function updateMetadata(track) {
  if (!('mediaSession' in navigator)) return;
  const folder = leaf(parentOf(track.path) || '');
  const set = (title, artist, album) => {
    navigator.mediaSession.metadata = new MediaMetadata({
      title, artist: artist || '', album: album || folder,
      artwork: [{ src: '/app/icon-512.png', sizes: '512x512', type: 'image/png' }],
    });
  };
  set(shownName(track.name, false), '', folder);
  try {
    const r = await api(mediaUrl('/api/details', track.path).replace(/&code=[^&]*/, ''));
    if (current() !== track) return;
    const tags = {};
    for (const t of (r.media && r.media.tags) || []) tags[t.name.toLowerCase()] = t.value;
    const legacy = r.tags || {};
    set(tags.title || legacy.title || shownName(track.name, false), tags.artist || legacy.artist, tags.album || legacy.album || folder);
  } catch { }
}

function setupMediaSession() {
  if (!('mediaSession' in navigator)) return;
  const ms = navigator.mediaSession;
  const on = (name, fn) => { try { ms.setActionHandler(name, fn); } catch { } };
  on('play', () => audio().play());
  on('pause', () => audio().pause());
  on('previoustrack', () => step(-1));
  on('nexttrack', () => step(1));
  on('seekbackward', (d) => skip(-(d && d.seekOffset || prefs.skipBack)));
  on('seekforward', (d) => skip(d && d.seekOffset || prefs.skipFwd));
  on('seekto', (d) => { if (d && d.seekTime != null) audio().currentTime = d.seekTime; });
}

function setupAudio() {
  const a = audio();
  let sliderShown = 0;
  a.addEventListener('loadedmetadata', () => {
    const at = Number(a.dataset.resume || 0);
    if (at > 5 && (!a.duration || at < a.duration - 10)) { a.currentTime = at; announce(`Resuming at ${spokenTime(at)}`); }
    a.dataset.resume = '0';
    if (a.playbackRate !== prefs.speed) a.playbackRate = prefs.speed;
    updatePosition();
    syncSession();
  });
  // A tick: the cached number only, the position saved now and then, and the
  // slider refreshed every 5 seconds while VoiceOver is actually on it.
  a.addEventListener('timeupdate', () => {
    savePosition(false);
    if (document.activeElement === $('np-position')) {
      const now = Date.now();
      if (now - sliderShown >= 5000) { sliderShown = now; updatePosition(); }
    }
  });
  a.addEventListener('seeked', () => { updatePosition(); syncSession(); });
  a.addEventListener('ratechange', syncSession);
  a.addEventListener('play', () => { renderPlayer(); syncSession(); });
  a.addEventListener('pause', () => { savePosition(true); renderPlayer(); updatePosition(); syncSession(); });
  a.addEventListener('ended', () => {
    const t = current();
    if (t) store.remove('pos:' + t.path);
    if (player.sleepAtEnd) { player.sleepAtEnd = false; $('np-sleep').value = '0'; announce('Sleep timer: stopped at the end of the track'); renderPlayer(); return; }
    if (player.repeat === 'one') { a.currentTime = 0; a.play(); return; }
    step(1);
  });
  let retry = 0;
  a.addEventListener('error', () => {
    const t = current();
    if (!t) return;
    // Usually the connection dropped: try again from where it was, a few times.
    if (retry++ < 5) {
      const at = a.currentTime;
      setTimeout(async () => { await waitForNetwork(3000); loadTrack(player.index, true, at); }, 1500);
    } else {
      retry = 0;
      // Picked up again from here as soon as the connection is back.
      player.failed = true;
      player.failedAt = a.currentTime;
      announce(`Could not play ${t.name}. It carries on when the connection is back.`, true);
    }
  });
  a.addEventListener('playing', () => { retry = 0; });
}

function setSleep(value) {
  clearTimeout(player.sleepTimer);
  player.sleepAtEnd = false;
  if (value === 'end') { player.sleepAtEnd = true; announce('Stops at the end of this track'); return; }
  const minutes = Number(value);
  if (!minutes) { announce('Sleep timer off'); return; }
  player.sleepTimer = setTimeout(() => {
    audio().pause();
    $('np-sleep').value = '0';
    announce('Sleep timer: paused');
  }, minutes * 60000);
  announce(`Pauses in ${plural(minutes, 'minute', 'minutes')}`);
}

// ---------- Clipboard ----------

// What to call the device this page is open on. A browser cannot read the
// phone's own name, so it is the kind of device.
const UA = navigator.userAgent || '';
const IS_IPAD = /iPad/.test(UA) || (/Macintosh/.test(UA) && navigator.maxTouchPoints > 1);
const DEVICE = /iPhone/.test(UA) ? 'iPhone' : IS_IPAD ? 'iPad' : /Android|Mobile/.test(UA) ? 'this phone' : 'this device';

// The PC clipboard as last fetched, so Copy can act inside the tap itself:
// iOS only lets a page write the clipboard during the user's gesture.
let clipNow = null;

async function getClipboard() {
  const out = $('clip-now');
  const copy = $('clip-copy');
  copy.textContent = `Copy to ${DEVICE}`;
  try {
    const c = await api('/api/clipboard');
    clipNow = c;
    out.replaceChildren();
    $('clip-fallback').hidden = true;
    if (c.kind === 'text') {
      const pre = document.createElement('pre'); pre.className = 'clip'; pre.textContent = c.text;
      out.append(pre);
      copy.hidden = false;
      announce(`The PC clipboard has text: ${c.text.length > 200 ? c.text.slice(0, 200) + '…' : c.text}`);
    } else if (c.kind === 'files') {
      const files = c.files || [];
      const ul = document.createElement('ul'); ul.className = 'list';
      for (const p of files) {
        const li = document.createElement('li');
        const name = document.createElement('span'); name.className = 'main'; name.textContent = leaf(p);
        li.append(name);
        ul.append(li);
      }
      const p = document.createElement('p'); p.textContent = `${plural(files.length, 'file', 'files')} on the PC clipboard:`;
      out.append(p, ul);
      copy.hidden = files.length === 0;
      announce(`The PC clipboard has ${plural(files.length, 'file', 'files')}.`);
    } else if (c.kind === 'image') {
      const img = document.createElement('img'); img.className = 'clip'; img.alt = 'The image on the PC clipboard';
      img.src = codeQuery(`/api/clipboard/image?t=${Date.now()}`);
      out.append(img);
      copy.hidden = false;
      announce('The PC clipboard has an image.');
    } else {
      const p = document.createElement('p'); p.textContent = 'The PC clipboard is empty.';
      out.append(p);
      copy.hidden = true;
      announce('The PC clipboard is empty.');
    }
  } catch (err) {
    clipNow = null;
    copy.hidden = true;
    const p = document.createElement('p'); p.textContent = err.message;
    out.replaceChildren(p);
    announce(err.message, true);
  }
}

// Copy to iPhone: one button for whatever is on the PC clipboard.
function copyClipToPhone() {
  const c = clipNow;
  if (!c || c.kind === 'empty') { announce('The PC clipboard is empty.'); return; }
  if (c.kind === 'text') { copyTextToPhone(c.text); return; }
  if (c.kind === 'files') {
    const files = c.files || [];
    announce(`Copying ${plural(files.length, 'file', 'files')} to ${DEVICE}. See Transfers.`);
    for (const p of files) downloadToPhone(mediaUrl('/api/file', p), leaf(p));
    return;
  }
  if (c.kind === 'image') {
    announce(`Copying the image to ${DEVICE}. See Transfers.`);
    downloadToPhone(codeQuery(`/api/clipboard/image?t=${Date.now()}`), 'clipboard.png');
  }
}

function copyTextToPhone(text) {
  const ok = () => {
    $('clip-status').textContent = `Text copied to ${DEVICE}`;
    announce(`Text copied to ${DEVICE}`);
  };
  const refused = () => {
    // iOS can refuse a clipboard write; the text is then here to select and copy.
    const box = $('clip-fallback-text');
    box.value = text;
    $('clip-fallback').hidden = false;
    $('clip-status').textContent = `${DEVICE === 'iPhone' ? 'The iPhone' : 'This device'} did not allow copying. ` +
      'The text is in the box below: press Copy, or select it and copy it yourself.';
    announce($('clip-status').textContent, true);
    box.focus();
    box.select();
  };
  // Called straight from the tap, so iOS counts it as the user's own copy.
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(ok, () => (legacyCopy(text) ? ok() : refused()));
  } else if (legacyCopy(text)) ok(); else refused();
}

// The older way, which iOS still honours inside a tap.
function legacyCopy(text) {
  try {
    const t = document.createElement('textarea');
    t.value = text;
    t.setAttribute('readonly', '');
    t.style.position = 'fixed'; t.style.top = '0'; t.style.opacity = '0';
    document.body.append(t);
    t.select();
    t.setSelectionRange(0, text.length);
    const done = document.execCommand('copy');
    t.remove();
    return done;
  } catch { return false; }
}

// A file to the phone, with progress in Transfers, then handed to Safari's
// download sheet the way Save does. Very big files go straight to Safari's own
// downloader instead of through memory.
const MEMORY_DOWNLOAD_LIMIT = 512 * 1024 * 1024;

async function downloadToPhone(url, name) {
  const t = addTransfer({ kind: 'download', name, state: 'running', done: 0, total: 0 });
  const ctrl = new AbortController();
  let stopped = false;
  t.cancel = async () => { stopped = true; ctrl.abort(); finishTransfer(t, 'cancelled'); };
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, { cache: 'no-store', signal: ctrl.signal });
      if (!res.ok) {
        const data = await res.json().catch(() => null);
        throw new ApiError(res.status, (data && data.error) || `The PC answered ${res.status}.`);
      }
      t.total = Number(res.headers.get('content-length')) || 0;
      if (t.total > MEMORY_DOWNLOAD_LIMIT || !res.body) {
        ctrl.abort();
        handToSafari(url, name);
        finishTransfer(t, 'done', `Saved to ${DEVICE} through Safari's downloads`);
        return;
      }
      const reader = res.body.getReader();
      const parts = [];
      t.done = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        parts.push(value);
        t.done += value.length;
        transferProgress(t);
        renderTransfers();
      }
      if (stopped) return;
      const blob = new Blob(parts, { type: res.headers.get('content-type') || 'application/octet-stream' });
      const href = URL.createObjectURL(blob);
      handToSafari(href, name);
      setTimeout(() => URL.revokeObjectURL(href), 60000);
      t.done = t.total = blob.size;
      finishTransfer(t, 'done', `Saved to ${DEVICE}`);
      return;
    } catch (err) {
      if (stopped) return;
      const network = err.name === 'TypeError' || err.status === 0;
      if (!network || attempt >= 6) { finishTransfer(t, 'failed', network ? UNREACHABLE : err.message); return; }
      t.state = 'waiting';
      renderTransfers();
      await waitForNetwork(Math.min(30000, 2000 * attempt));
      t.state = 'running';
    }
  }
}

function handToSafari(href, name) {
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
}

async function sendText(text) {
  if (!text) { announce('There is no text to send.'); return; }
  try {
    await api('/api/clipboard', { method: 'POST', body: { text } });
    $('clip-status').textContent = 'Sent to the PC clipboard';
    announce('Sent to the PC clipboard');
  } catch (err) { announce(err.message, true); }
}

async function sendFiles(files) {
  const batch = `web${Date.now()}`;
  try {
    for (const f of files) {
      announce(`Sending ${f.name}`);
      await api(`/api/clipboard/send?name=${encodeURIComponent(f.name)}&batch=${batch}`,
        { method: 'POST', body: f, raw: true, timeout: 300000 });
    }
    await api('/api/clipboard/send/commit', { method: 'POST', body: { batch } });
    $('clip-status').textContent = 'Sent to the PC clipboard';
    announce(`Sent to the PC clipboard: ${plural(files.length, 'file', 'files')}. Paste with Control V.`);
  } catch (err) { announce(err.message, true); }
}

// ---------- Settings ----------

function fillSelect(select, values, text, selected) {
  select.replaceChildren(...values.map((v) => {
    const o = document.createElement('option'); o.value = String(v); o.textContent = text(v);
    if (v === selected) o.selected = true;
    return o;
  }));
}

function renderSettings() {
  $('set-who').textContent = state.trusted
    ? `Signed in through Tailscale${state.user ? ' as ' + state.user : ''}. No pairing code is needed.`
    : 'Using the pairing code for this PC.';
  $('set-sort').value = prefs.sort;
  $('set-folders-first').checked = prefs.foldersFirst;
  $('set-extensions').checked = prefs.extensions;
  $('set-confirm-delete').checked = prefs.confirmDelete;
  $('set-conflict').value = prefs.conflict;
  $('set-announce-transfers').checked = prefs.announceTransfers;
  $('set-resume').checked = prefs.resume;
  $('set-play-rest').checked = prefs.playRest;
  $('set-continue').checked = prefs.continue;
  $('set-forget').hidden = state.trusted || !state.code;
  $('set-version').textContent = `Explorer Connect on ${state.computer || 'the PC'}.`;
}

function bindSettings() {
  const skips = [5, 10, 15, 30, 45, 60, 120, 300];
  fillSelect($('set-skip-back'), skips, (s) => spokenTime(s), prefs.skipBack);
  fillSelect($('set-skip-fwd'), skips, (s) => spokenTime(s), prefs.skipFwd);
  fillSelect($('set-speed'), SPEEDS, speedText, prefs.speed);
  fillSelect($('np-speed'), SPEEDS, speedText, prefs.speed);
  const bind = (id, key, read) => $(id).addEventListener('change', (e) => {
    prefs[key] = read(e.target);
    savePrefs();
    if (['sort', 'foldersFirst', 'extensions'].includes(key)) renderFiles();
    if (key === 'speed') { audio().playbackRate = prefs.speed; $('np-speed').value = String(prefs.speed); }
    renderPlayer();
  });
  bind('set-sort', 'sort', (t) => t.value);
  bind('set-folders-first', 'foldersFirst', (t) => t.checked);
  bind('set-extensions', 'extensions', (t) => t.checked);
  bind('set-confirm-delete', 'confirmDelete', (t) => t.checked);
  bind('set-conflict', 'conflict', (t) => t.value);
  bind('set-announce-transfers', 'announceTransfers', (t) => t.checked);
  bind('set-skip-back', 'skipBack', (t) => Number(t.value));
  bind('set-skip-fwd', 'skipFwd', (t) => Number(t.value));
  bind('set-speed', 'speed', (t) => Number(t.value));
  bind('set-resume', 'resume', (t) => t.checked);
  bind('set-play-rest', 'playRest', (t) => t.checked);
  bind('set-continue', 'continue', (t) => t.checked);
  $('np-speed').addEventListener('change', (e) => {
    prefs.speed = Number(e.target.value); savePrefs();
    audio().playbackRate = prefs.speed; $('set-speed').value = String(prefs.speed);
    announce(speedText(prefs.speed));
  });
  $('set-forget').addEventListener('click', () => {
    state.code = ''; store.remove('code'); announce('The pairing code is forgotten.');
    showCodeScreen();
  });
}

// ---------- Start ----------

async function whoami() {
  const res = await timedFetch('/api/whoami', { headers: state.code ? { 'X-Connect-Code': state.code } : {} }, 15000);
  if (!res.ok) throw new Error(res.status === 502 || res.status === 503
    ? 'The PC answered, but Explorer Native is not running there. Start it, then try again.'
    : UNREACHABLE);
  return res.json();
}

// Who this is, then the files; or the code screen, or the offline screen.
async function connect() {
  let who;
  try { who = await whoami(); }
  catch (e) { showOffline(e && e.message && e.name !== 'AbortError' && e.name !== 'TypeError' ? e.message : UNREACHABLE); return; }
  state.trusted = !who.needsCode;
  state.user = who.user;
  state.computer = who.computer;
  if (!state.trusted && !who.codeOk) {
    showCodeScreen(state.code ? 'That pairing code is not right any more. Enter the one shown on the PC.' : '');
    return;
  }
  await enter();
}

async function start() {
  bindEvents();
  bindSettings();
  setupAudio();
  setupMediaSession();
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { });
  await connect();
}

async function enter() {
  try {
    const f = await api('/api/formats');
    if (f.audio) state.audio = new Set(f.audio.map((x) => x.toLowerCase()));
    if (f.native) for (const n of f.native) state.native.add(n.toLowerCase());
  } catch { }
  showTab('files', false);
  history.replaceState({ path: null }, '');
  const last = store.get('lastPath', null);
  if (last) {
    history.pushState({ path: last }, '');
    await openFolder(last, { push: false });
  } else {
    await openFolder(null, { push: false });
  }
  renderPlayer();
}

function bindEvents() {
  for (const b of document.querySelectorAll('#tabs button')) b.addEventListener('click', () => showTab(b.dataset.tab));
  // A tab bar is one stop for the keyboard; the arrows move along it.
  $('tabs').addEventListener('keydown', (ev) => {
    const tabs = [...document.querySelectorAll('#tabs button')];
    const i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    const to = ev.key === 'ArrowRight' ? (i + 1) % tabs.length : ev.key === 'ArrowLeft' ? (i + tabs.length - 1) % tabs.length
      : ev.key === 'Home' ? 0 : ev.key === 'End' ? tabs.length - 1 : -1;
    if (to < 0) return;
    ev.preventDefault();
    showTab(tabs[to].dataset.tab, false);
    tabs[to].focus();
  });
  $('main').addEventListener('scroll', onMainScroll, { passive: true });
  $('code-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const code = $('code-input').value.replace(/[\s-]/g, '');
    const res = await timedFetch('/api/whoami', { headers: { 'X-Connect-Code': code } }, 15000).then((r) => r.json()).catch(() => null);
    if (!res) { $('code-error').textContent = UNREACHABLE; announce(UNREACHABLE, true); return; }
    if (!res.codeOk) { $('code-error').textContent = 'That code is not right. Check it on the PC and try again.'; $('code-input').focus(); return; }
    state.code = code;
    store.set('code', code);
    state.computer = res.computer;
    announce('Connected');
    await enter();
  });
  $('btn-up').addEventListener('click', () => {
    if (state.path == null) return;
    const came = leaf(state.path) || state.path;
    const up = parentOf(state.path);
    openFolder(up, { focusName: up == null ? state.path : came });
  });
  $('btn-refresh').addEventListener('click', () => openFolder(state.path, { push: false }));
  $('btn-play-folder').addEventListener('click', () => {
    const tracks = sorted(state.entries).filter((x) => !x.folder && isAudio(x.name));
    if (!tracks.length) { announce('There is nothing to play in this folder.'); return; }
    playQueue(tracks.map((x) => ({ path: join(state.path, x.name), name: x.name })), 0);
  });
  $('btn-new-folder').addEventListener('click', newFolder);
  $('btn-upload').addEventListener('click', () => $('upload-input').click());
  $('upload-input').addEventListener('change', (e) => { const files = [...e.target.files]; e.target.value = ''; if (files.length) uploadFiles(files); });
  $('btn-paste').addEventListener('click', paste);
  $('btn-select').addEventListener('click', () => { state.selectMode = true; state.selected.clear(); renderFiles(); announce('Select items, then choose an action.'); });
  $('btn-sel-done').addEventListener('click', () => { state.selectMode = false; renderFiles(); $('files-title').focus(); });
  const selected = () => { const p = [...state.selected]; if (!p.length) announce('Nothing is selected.'); return p; };
  $('btn-sel-copy').addEventListener('click', () => { const p = selected(); if (p.length) { state.selectMode = false; hold(p, false); } });
  $('btn-sel-cut').addEventListener('click', () => { const p = selected(); if (p.length) { state.selectMode = false; hold(p, true); } });
  $('btn-sel-pc').addEventListener('click', () => { const p = selected(); if (p.length) copyOnPc(p); });
  $('btn-sel-delete').addEventListener('click', () => { const p = selected(); if (p.length) remove(p); });

  $('np-play').addEventListener('click', togglePlay);
  $('np-resume').addEventListener('click', () => {
    const s = store.get('session', null);
    if (!s) return;
    player.queue = s.queue; player.order = s.queue.map((_, i) => i);
    loadTrack(s.index, true);
    $('np-play').focus();
  });
  $('np-prev').addEventListener('click', () => {
    const a = audio();
    if (a.currentTime > 5) { a.currentTime = 0; announce('Start of the track'); } else step(-1);
  });
  $('np-next').addEventListener('click', () => step(1));
  $('np-back').addEventListener('click', () => skip(-prefs.skipBack));
  $('np-fwd').addEventListener('click', () => skip(prefs.skipFwd));
  $('np-position').addEventListener('change', (e) => { audio().currentTime = Number(e.target.value); });
  $('np-position').addEventListener('focus', updatePosition);
  $('np-time-btn').addEventListener('click', sayTime);
  $('mini-play').addEventListener('click', togglePlay);
  $('mini-title').addEventListener('click', () => showTab('player'));
  $('np-repeat').addEventListener('change', (e) => { player.repeat = e.target.value; });
  $('np-shuffle').addEventListener('change', (e) => {
    player.shuffle = e.target.checked;
    if (player.shuffle && player.index >= 0) shuffleOrder(player.index); else player.order = player.queue.map((_, i) => i);
    announce(player.shuffle ? 'Shuffle on' : 'Shuffle off');
  });
  $('np-sleep').addEventListener('change', (e) => setSleep(e.target.value));

  $('tr-clear').addEventListener('click', () => {
    for (let i = transfers.length - 1; i >= 0; i--) if (!['running', 'waiting'].includes(transfers[i].state)) transfers.splice(i, 1);
    renderTransfers(); announce('Cleared');
  });
  $('tr-stop').addEventListener('click', () => {
    for (const t of transfers) if (['running', 'waiting'].includes(t.state) && t.cancel) t.cancel();
    announce('Stopping all transfers');
  });

  $('clip-get').addEventListener('click', getClipboard);
  $('clip-copy').addEventListener('click', copyClipToPhone);
  $('clip-fallback-copy').addEventListener('click', () => {
    const box = $('clip-fallback-text');
    box.focus(); box.select(); box.setSelectionRange(0, box.value.length);
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { }
    const m = ok ? `Text copied to ${DEVICE}` : 'Copying is not allowed here. The text is selected: use the Copy option above it.';
    $('clip-status').textContent = m;
    announce(m, !ok);
  });
  $('offline-retry').addEventListener('click', async () => {
    $('offline-text').textContent = 'Trying again…';
    announce('Trying again');
    await connect();
  });
  $('clip-send').addEventListener('click', () => sendText($('clip-text').value));
  $('clip-send-files').addEventListener('click', () => $('clip-files').click());
  $('clip-files').addEventListener('change', (e) => { const files = [...e.target.files]; e.target.value = ''; if (files.length) sendFiles(files); });

  window.addEventListener('popstate', (e) => {
    if (!e.state || !('path' in e.state)) return;
    const from = state.path;
    openFolder(e.state.path, { push: false, focusName: from ? leaf(from) || from : null });
  });
  window.addEventListener('online', () => {
    pollJobs();
    if (player.failed && current()) { player.failed = false; loadTrack(player.index, true, player.failedAt); }
    if (!$('screen-offline').hidden) connect();
  });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { savePosition(true); clearTimeout(jobTimer); }
    else pollJobs();
  });
  window.addEventListener('pagehide', () => savePosition(true));
}

// Anything not caught where it happened is still said, in a plain sentence,
// and never leaves the page stuck.
window.addEventListener('unhandledrejection', (e) => {
  const m = e.reason && e.reason.message ? e.reason.message : 'Something went wrong.';
  announce(m, true);
  e.preventDefault();
});
window.addEventListener('error', (e) => {
  announce('Something went wrong: ' + (e.message || 'unknown problem') + '. Try again, or reload the page.', true);
});

start().catch((e) => showOffline(e && e.message ? e.message : UNREACHABLE));
