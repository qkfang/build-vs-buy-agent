'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const $all = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const fmtMoney = (n, ccy) => {
  const sym = { USD: '$', AUD: 'A$', EUR: '€', GBP: '£' }[ccy] || (ccy + ' ');
  return sym + Number(n || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
};
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// ---------------------------------------------------------------- store
const JOB_STORE_KEY = 'proj37.currentJob';
const SESSION_STORE_KEY = 'proj37.currentSession';
const Store = {
  getJob() { try { return JSON.parse(localStorage.getItem(JOB_STORE_KEY) || 'null'); } catch { return null; } },
  setJob(job) { try { localStorage.setItem(JOB_STORE_KEY, JSON.stringify(job)); } catch { /* ignore quota */ } },
  clearJob() { try { localStorage.removeItem(JOB_STORE_KEY); } catch { /* ignore */ } },
  getSession() { try { return JSON.parse(localStorage.getItem(SESSION_STORE_KEY) || 'null'); } catch { return null; } },
  setSession(session) { try { localStorage.setItem(SESSION_STORE_KEY, JSON.stringify(session)); } catch { /* ignore quota */ } },
  clearSession() { try { localStorage.removeItem(SESSION_STORE_KEY); } catch { /* ignore */ } },
};

let AGENT_INSTRUCTIONS = null;

// ---------------------------------------------------------------- bootstrap
document.addEventListener('DOMContentLoaded', () => {
  wireModal();
  wireAgentStepButtons();
  const page = document.body.dataset.page || '';
  if (page === 'upload') initUpload();
  else if (page === 'scope') initScope();
  else if (page === 'requirements') initRequirements();
  else if (page === 'cost') initCost();
  else if (page === 'project') initProjectCost();
  else if (page === 'operations') initOperations();
  else if (page === 'steps') initSteps();
  else if (page === 'compare') initCompare();
  else if (page === 'estimations') initEstimations();
});

// ================================================================ UPLOAD page
let selectedFiles = [];

function initUpload() {
  wireUpload();
  loadSamples();
  loadPreviousSessions();
  const session = Store.getSession();
  if (session && session.sessionId) showSessionReady(session);
}

function wireUpload() {
  const input = $('#fileInput');
  const dz = $('#dropzone');
  input.addEventListener('change', () => { selectedFiles = Array.from(input.files); renderFileList(); });
  ['dragover', 'dragenter'].forEach(ev => dz.addEventListener(ev, (e) => { e.preventDefault(); dz.classList.add('drag'); }));
  ['dragleave', 'drop'].forEach(ev => dz.addEventListener(ev, (e) => { e.preventDefault(); dz.classList.remove('drag'); }));
  dz.addEventListener('drop', (e) => { selectedFiles = Array.from(e.dataTransfer.files); renderFileList(); });

  $('#uploadForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    if (selectedFiles.length === 0) { setStatus('Please choose at least one document, or run an example brief below.', 'error'); return; }
    const fd = new FormData();
    selectedFiles.forEach(f => fd.append('files', f));
    await createSession(() => fetch('/api/sessions', { method: 'POST', body: fd }));
  });

  const sessionSelect = $('#previousSessionSelect');
  if (sessionSelect) {
    sessionSelect.addEventListener('change', () => {
      const loadBtn = $('#loadSessionBtn');
      if (loadBtn) loadBtn.disabled = !sessionSelect.value;
    });
  }

  const loadBtn = $('#loadSessionBtn');
  if (loadBtn) {
    loadBtn.addEventListener('click', async () => {
      const sessionId = sessionSelect?.value;
      if (!sessionId) { setStatus('Choose a previous session to load.', 'error'); return; }
      const session = await fetchSession(sessionId);
      if (!session) { setStatus('Could not load that session.', 'error'); return; }
      Store.setSession(session);
      Store.clearJob();
      window.location.href = '/platform/scope';
    });
  }
}

function renderFileList() {
  const list = $('#fileList');
  if (selectedFiles.length === 0) { list.innerHTML = ''; $('#dropLabel').textContent = 'Click to choose files or drag & drop'; return; }
  $('#dropLabel').textContent = selectedFiles.length + ' file(s) selected';
  list.innerHTML = selectedFiles.map(f =>
    `<div class="file-chip"><span>📄 ${esc(f.name)}</span><span class="muted">${(f.size / 1024).toFixed(1)} KB</span></div>`).join('');
}

async function loadSamples() {
  const el = $('#sampleList');
  if (!el) return;
  try {
    const r = await fetch('/api/samples');
    const items = await r.json();
    if (!items.length) { el.innerHTML = '<p class="muted">No example documents available.</p>'; return; }
    el.innerHTML = items.map(s => `
      <div class="sample-item">
        <div class="sample-meta">
          <span class="sample-title">📑 ${esc(s.title)}</span>
          <span class="muted sample-file">${esc(s.fileName)} · ${(s.sizeBytes / 1024).toFixed(1)} KB</span>
        </div>
        <div class="sample-actions">
          <button type="button" class="btn btn-secondary btn-sm" data-view-sample="${esc(s.id)}" data-title="${esc(s.title)}">View</button>
          <button type="button" class="btn btn-primary btn-sm" data-use-sample="${esc(s.id)}" data-file-name="${esc(s.fileName)}">Use this</button>
        </div>
      </div>`).join('');
    $all('[data-view-sample]', el).forEach(b => b.addEventListener('click', () => viewSample(b.dataset.viewSample, b.dataset.title)));
    $all('[data-use-sample]', el).forEach(b => b.addEventListener('click', () => useSample(b.dataset.useSample, b.dataset.fileName)));
  } catch {
    el.innerHTML = '<p class="muted">Could not load example documents.</p>';
  }
}

async function viewSample(id, title) {
  openModal(title || 'Example brief', '<p class="muted">Loading…</p>');
  try {
    // Prefer server-rendered HTML (Markdig) so the popup shows nicely formatted content, not raw markdown.
    const r = await fetch('/api/samples/' + encodeURIComponent(id) + '/html');
    if (r.ok) {
      const html = await r.text();
      setModalBody(`<div class="doc-html">${html}</div>`);
      return;
    }
    // Fallback: raw markdown as preformatted text.
    const raw = await fetch('/api/samples/' + encodeURIComponent(id));
    if (!raw.ok) { setModalBody('<p class="muted">Could not load this document.</p>'); return; }
    const md = await raw.text();
    setModalBody(`<pre class="doc-md">${esc(md)}</pre>`);
  } catch {
    setModalBody('<p class="muted">Could not load this document.</p>');
  }
}

async function useSample(id, fileName) {
  await createSession(async () => {
    const raw = await fetch('/api/samples/' + encodeURIComponent(id));
    if (!raw.ok) throw new Error('Could not load sample document.');
    const text = await raw.text();
    const fd = new FormData();
    fd.append('files', new Blob([text], { type: 'text/markdown' }), fileName || (id + '.md'));
    return fetch('/api/sessions', { method: 'POST', body: fd });
  });
}

async function createSession(call) {
  setBusy(true);
  setStatus('Ingesting documents and creating a session…', 'busy');
  try {
    const r = await call();
    const session = await r.json();
    if (!r.ok) {
      setStatus('Session creation failed: ' + (session.error || r.statusText), 'error');
      return;
    }
    setStatus('Session created. Open Scope and click Run agent to start the pipeline.', 'info');
    Store.setSession(session);
    Store.clearJob();
    showSessionReady(session);
    await loadPreviousSessions();
  } catch (err) {
    setStatus('Request error: ' + err.message, 'error');
  } finally {
    setBusy(false);
  }
}

async function loadPreviousSessions() {
  const sel = $('#previousSessionSelect');
  if (!sel) return;
  try {
    const r = await fetch('/api/sessions');
    const items = await r.json();
    if (!items.length) {
     sel.innerHTML = '<option value="">No previous sessions</option>';
     sel.disabled = true;
     const loadBtn = $('#loadSessionBtn'); if (loadBtn) loadBtn.disabled = true;
     return;
    }
    const current = Store.getSession()?.sessionId || '';
    sel.innerHTML = '<option value="">Select a previous session…</option>' + items.map(s =>
     `<option value="${esc(s.sessionId)}"${s.sessionId === current ? ' selected' : ''}>${esc(s.project || s.sessionId)} · ${esc(s.status)} · ${new Date(s.createdUtc).toLocaleString()}</option>`).join('');
    sel.disabled = false;
    const loadBtn = $('#loadSessionBtn'); if (loadBtn) loadBtn.disabled = !current;
  } catch {
    sel.innerHTML = '<option value="">Could not load sessions</option>';
    sel.disabled = true;
    const loadBtn = $('#loadSessionBtn'); if (loadBtn) loadBtn.disabled = true;
  }
}

function showSessionReady(session) {
  const card = $('#doneCard');
  if (!card) return;
  card.hidden = false;
  $('#doneSummary').textContent =
    `${session.documents?.length || 0} document(s) ingested into ${session.sessionId}. No agent step has run yet — open Scope and click Run agent.`;
  card.scrollIntoView({ behavior: 'smooth' });
}

function setStatus(msg, kind) { const s = $('#status'); if (!s) return; s.hidden = false; s.textContent = msg; s.className = 'status ' + kind; }
function setBusy(b) {
  const e = $('#estimateBtn');
  if (e) e.disabled = b;
  const loadBtn = $('#loadSessionBtn');
  const sel = $('#previousSessionSelect');
  if (loadBtn) loadBtn.disabled = b || !sel || !sel.value;
  $all('[data-use-sample]').forEach(x => x.disabled = b);
}

// ================================================================ PLATFORM pages
function platformContext(mode, data) {
  const line = $('#ctxLine');
  if (!line) return;
  if (!data) { line.textContent = 'No estimation or session loaded yet.'; return; }
  if (mode === 'session') {
    const cost = data.cost || {};
    const total = data.cost ? `${fmtMoney(cost.monthlyTotalWithContingency, cost.currency)}/mo · ` : '';
    line.innerHTML = `<strong>${esc(data.scope?.projectName || 'New session')}</strong> · ${total}<span class="muted">${esc(data.sessionId)}</span>`;
    return;
  }
  if (!data.scope) { line.textContent = 'No estimation loaded yet.'; return; }
  const c = data.cost || {};
  line.innerHTML = `<strong>${esc(data.scope.projectName || 'Estimation')}</strong> · `
    + `${fmtMoney(c.monthlyTotalWithContingency, c.currency)}/mo · <span class="muted">job ${esc(data.jobId)}</span>`;
}

function showOrEmpty(data, cardSel) {
  const empty = $('#emptyState');
  const card = $(cardSel);
  if (!data) { if (empty) empty.hidden = false; if (card) card.hidden = true; return false; }
  if (empty) empty.hidden = true;
  if (card) card.hidden = false;
  return true;
}

// Returns a job that satisfies `hasData`. If the locally-stored job is missing the data (e.g. a stale
// job stored before Project/Operation cost existed), re-fetch the authoritative job from the server —
// which always generates it — and refresh the local store so every tab renders consistently.
async function ensureJobDetail(job, hasData) {
  if (!job || !job.jobId || hasData(job)) return job;
  try {
    const r = await fetch('/api/estimations/' + encodeURIComponent(job.jobId));
    if (r.ok) {
      const fresh = await r.json();
      if (fresh && fresh.jobId) { Store.setJob(fresh); return fresh; }
    }
  } catch { /* keep the local copy on any error */ }
  return job;
}

async function fetchSession(sessionId) {
  if (!sessionId) return null;
  try {
    const r = await fetch('/api/sessions/' + encodeURIComponent(sessionId));
    if (!r.ok) return null;
    const session = await r.json();
    if (session?.sessionId) Store.setSession(session);
    return session;
  } catch { return null; }
}

async function loadPlatformState(hasJobData) {
  const sessionRef = Store.getSession();
  if (sessionRef?.sessionId) {
    const session = await fetchSession(sessionRef.sessionId) || sessionRef;
    return { mode: 'session', data: session };
  }
  let job = Store.getJob();
  if (job) {
    job = await ensureJobDetail(job, hasJobData || (() => true));
    if (job?.jobId) Store.setJob(job);
    return { mode: 'job', data: job };
  }
  return { mode: null, data: null };
}

function stepState(session, step) {
  return session?.steps?.[step] || { status: 'pending', lastRunUtc: null, error: null };
}

function renderSessionStepMeta(session, step) {
  const state = stepState(session, step);
  const status = $('#stepStatus');
  const error = $('#stepError');
  if (status) {
    const lastRun = state.lastRunUtc ? new Date(state.lastRunUtc).toLocaleString() : 'Never';
    status.innerHTML = `<strong>Status:</strong> ${esc(state.status)} · <span class="muted">Last run: ${esc(lastRun)}</span>`;
  }
  if (error) {
    if (state.error) {
      error.hidden = false;
      error.textContent = state.error;
    } else {
      error.hidden = true;
      error.textContent = '';
    }
  }
}

function wireRunButton(selector, mode, handler) {
  const btn = $(selector);
  if (!btn) return;
  if (mode !== 'session') { btn.hidden = true; return; }
  btn.hidden = false;
  if (btn.dataset.wired) return;
  btn.dataset.wired = '1';
  btn.addEventListener('click', handler);
}

async function postSessionStep(step, buttonSelector, runningLabel) {
  const session = Store.getSession();
  if (!session?.sessionId) throw new Error('No session loaded.');
  const btn = $(buttonSelector);
  const original = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = runningLabel; }
  try {
    const r = await fetch(`/api/sessions/${encodeURIComponent(session.sessionId)}/steps/${encodeURIComponent(step)}`, { method: 'POST' });
    const payload = await r.json();
    if (!r.ok) throw new Error(payload.error || r.statusText);
    Store.setSession(payload);
    return payload;
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = original; }
  }
}

async function initScope() {
  const { mode, data } = await loadPlatformState(j => !!j.scope);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#scopeCard')) return;
  wireRunButton('#runScopeBtn', mode, async () => {
    try {
      const session = await postSessionStep('scope', '#runScopeBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'scope');
      renderScopeOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'scope');
    renderScopeOrPending(data);
    return;
  }
  renderScope(data.scope || {});
}

async function initRequirements() {
  const { mode, data } = await loadPlatformState(j => Array.isArray(j.requirements) && j.requirements.length > 0);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#reqCard')) return;
  wireRunButton('#runRequirementsBtn', mode, async () => {
    try {
      const session = await postSessionStep('requirements', '#runRequirementsBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'requirements');
      renderRequirementsOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'requirements');
    renderRequirementsOrPending(data);
    return;
  }
  renderRequirements(data.requirements || []);
}

async function initCost() {
  const { mode, data } = await loadPlatformState(j => ((j.cost && j.cost.lineItems) || []).length > 0);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#costCard')) return;
  wireRunButton('#runCostBtn', mode, async () => {
    try {
      const session = await postSessionStep('cost', '#runCostBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'cost');
      renderCostOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'cost');
    renderCostOrPending(data);
    return;
  }
  const dl = $('#downloadBtn');
  if (dl) { dl.hidden = false; dl.href = `/api/estimations/${data.jobId}/workbook`; dl.setAttribute('download', ''); }
  renderCost(data.cost || {});
}

async function initSteps() {
  const { mode, data } = await loadPlatformState(() => true);
  platformContext(mode, data);
  renderStepCards();
  if (!showOrEmpty(data, '#stepsCard')) return;
  renderSteps(data.agentSteps || [], data.steps || null);
}

// ================================================================ PROJECT (build) cost page
// One-time delivery cost: roles with an editable Day rate and Estimated days; Cost = rate * days.
let PROJECT_STATE = null;

async function initProjectCost() {
  const { mode, data } = await loadPlatformState(j => ((j.projectCost && j.projectCost.roles) || []).length > 0);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#projectCard')) return;
  const teamCard = $('#teamStructureCard'); if (teamCard) teamCard.hidden = false;
  wireTeamStructurePicker();
  wireRunButton('#runProjectBtn', mode, async () => {
    try {
      const session = await postSessionStep('project', '#runProjectBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'project');
      renderProjectOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'project');
    renderProjectOrPending(data);
    return;
  }
  const dl = $('#downloadBtn');
  if (dl) { dl.hidden = false; dl.href = `/api/estimations/${data.jobId}/workbook`; dl.setAttribute('download', ''); }
  renderProjectCost(data.projectCost || {});
}

// ---- Team structure configuration: reuse a previous project's roles/rates into the grid.
let TEAM_STRUCTURES = null;
let TEAM_STRUCTURE_WIRED = false;

async function wireTeamStructurePicker() {
  const sel = $('#teamStructureSelect');
  const btn = $('#loadTeamStructureBtn');
  if (!sel || !btn) return;
  if (!TEAM_STRUCTURE_WIRED) {
    TEAM_STRUCTURE_WIRED = true;
    sel.addEventListener('change', () => { btn.disabled = !sel.value; });
    btn.addEventListener('click', () => applyTeamStructure(sel.value));
  }
  await loadTeamStructures();
}

async function loadTeamStructures() {
  const sel = $('#teamStructureSelect');
  const btn = $('#loadTeamStructureBtn');
  if (!sel) return;
  try {
    if (!TEAM_STRUCTURES) {
      const r = await fetch('/data/team-structures.json');
      if (!r.ok) throw new Error('failed to load');
      TEAM_STRUCTURES = await r.json();
    }
    if (!TEAM_STRUCTURES.length) {
      sel.innerHTML = '<option value="">No previous team structures available</option>';
      sel.disabled = true;
      if (btn) btn.disabled = true;
      return;
    }
    sel.innerHTML = '<option value="">Select a previous team structure…</option>' + TEAM_STRUCTURES.map(t =>
      `<option value="${esc(t.id)}">${esc(t.name)} (${t.roles.length} roles, ${esc(t.currency)})</option>`).join('');
    sel.disabled = false;
    if (btn) btn.disabled = true;
  } catch {
    sel.innerHTML = '<option value="">Could not load team structures</option>';
    sel.disabled = true;
    if (btn) btn.disabled = true;
  }
}

function applyTeamStructure(id) {
  const tmpl = (TEAM_STRUCTURES || []).find(t => t.id === id);
  const hint = $('#teamStructureHint');
  if (!tmpl) return;
  if (!PROJECT_STATE) PROJECT_STATE = {};
  PROJECT_STATE.currency = tmpl.currency || PROJECT_STATE.currency || 'USD';
  PROJECT_STATE.contingencyPercent = tmpl.contingencyPercent ?? PROJECT_STATE.contingencyPercent ?? 15;
  PROJECT_STATE.notes = PROJECT_STATE.notes || [];
  PROJECT_STATE.roles = tmpl.roles.map(r => ({
    role: r.role, description: r.description,
    dayRate: Number(r.dayRate) || 0, estimatedDays: Number(r.estimatedDays) || 0,
  }));
  renderProjectCost(PROJECT_STATE);
  if (hint) hint.textContent = `Loaded "${tmpl.name}" — adjust day rates and estimated days below as needed.`;
}

function renderProjectCost(p) {
  PROJECT_STATE = p;
  const roles = p.roles || [];
  roles.forEach(r => { r.cost = Math.round(Number(r.dayRate || 0) * Number(r.estimatedDays || 0) * 100) / 100; });
  renderProjectTotals(p);
  if (!roles.length) { $('#tab-project').innerHTML = '<p class="muted">No delivery roles were generated for this estimation. <a href="/">Run the estimate again</a> to generate the project (build) cost.</p>'; return; }
  const rows = roles.map((r, idx) => `<tr>
      <td>${esc(r.role)}</td><td class="muted">${esc(r.description)}</td>
      <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="dayRate" value="${Number(r.dayRate)}" aria-label="Day rate for ${esc(r.role)}" /></td>
      <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="days" value="${Number(r.estimatedDays)}" aria-label="Estimated days for ${esc(r.role)}" /></td>
      <td class="num-col" data-cost="${idx}"><strong>${fmtMoney(r.cost, p.currency)}</strong></td></tr>`).join('');
  $('#tab-project').innerHTML = `
    <table><thead><tr><th>Role</th><th>Description</th>
      <th class="num-col">Day rate</th><th class="num-col">Est. days</th><th class="num-col">Cost</th></tr></thead>
    <tbody>${rows}</tbody>
    <tfoot><tr><th colspan="4" class="num-col">Total build cost (incl. <span>${p.contingencyPercent}</span>% contingency)</th>
      <th class="num-col" id="projectFootTotal">${fmtMoney(projectTotalWithContingency(p), p.currency)}</th></tr></tfoot></table>
    <p class="muted" style="margin-top:.7rem">${(p.notes || []).map(esc).join(' · ')}</p>`;
  $all('.qty-input', $('#tab-project')).forEach(inp => inp.addEventListener('input', onProjectEdit));
}

function projectLaborTotal(p) {
  return (p.roles || []).reduce((s, r) => s + Number(r.cost || 0), 0);
}

function projectTotalWithContingency(p) {
  const pct = Number(p.contingencyPercent || 0);
  return Math.round(projectLaborTotal(p) * (1 + pct / 100) * 100) / 100;
}

function onProjectEdit(e) {
  const idx = Number(e.target.dataset.row);
  const field = e.target.dataset.field;
  const p = PROJECT_STATE;
  if (!p || !p.roles[idx]) return;
  const val = Number(e.target.value);
  const safe = isFinite(val) && val >= 0 ? val : 0;
  const role = p.roles[idx];
  if (field === 'dayRate') role.dayRate = safe; else role.estimatedDays = safe;
  role.cost = Math.round(Number(role.dayRate || 0) * Number(role.estimatedDays || 0) * 100) / 100;
  const cell = $(`[data-cost="${idx}"]`); if (cell) cell.innerHTML = `<strong>${fmtMoney(role.cost, p.currency)}</strong>`;
  const foot = $('#projectFootTotal'); if (foot) foot.textContent = fmtMoney(projectTotalWithContingency(p), p.currency);
  renderProjectTotals(p);
}

function renderProjectTotals(p) {
  const el = $('#projectTotals');
  if (!el) return;
  const roles = p.roles || [];
  const pct = Number(p.contingencyPercent || 0);
  const labor = roles.reduce((s, r) => s + Number(r.cost || 0), 0);
  const days = roles.reduce((s, r) => s + Number(r.estimatedDays || 0), 0);
  const withCont = Math.round(labor * (1 + pct / 100) * 100) / 100;
  el.innerHTML = `
    <div class="total-box hi"><div class="num">${fmtMoney(withCont, p.currency)}</div><div class="lbl">Total build cost</div></div>
    <div class="total-box"><div class="num">${fmtMoney(labor, p.currency)}</div><div class="lbl">Labour (excl. contingency)</div></div>
    <div class="total-box"><div class="num">${Math.round(days * 10) / 10}</div><div class="lbl">Person-days</div></div>
    <div class="total-box"><div class="num">${roles.length}</div><div class="lbl">Roles</div></div>
    <div class="total-box"><div class="num">${pct}%</div><div class="lbl">Contingency</div></div>`;
}

// ================================================================ OPERATION (run) cost page
// Ongoing monthly cost: line items with an editable Qty and Unit price; Monthly = qty * unit price.
let OPERATIONS_STATE = null;

async function initOperations() {
  const { mode, data } = await loadPlatformState(j => ((j.operations && j.operations.items) || []).length > 0);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#operationsCard')) return;
  wireRunButton('#runOperationsBtn', mode, async () => {
    try {
      const session = await postSessionStep('operations', '#runOperationsBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'operations');
      renderOperationsOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'operations');
    renderOperationsOrPending(data);
    return;
  }
  const dl = $('#downloadBtn');
  if (dl) { dl.hidden = false; dl.href = `/api/estimations/${data.jobId}/workbook`; dl.setAttribute('download', ''); }
  renderOperations(data.operations || {});
}

function renderScopeOrPending(session) {
  if (session.scope) {
    renderScope(session.scope);
    return;
  }
  $('#tab-scope').innerHTML = '<p class="muted">Scope has not been generated yet. Click <strong>Run agent</strong> to analyze the uploaded documents.</p>';
}

function renderRequirementsOrPending(session) {
  const requirements = session.requirements || [];
  if (requirements.length) {
    renderRequirements(requirements);
    return;
  }
  $('#tab-requirements').innerHTML = '<p class="muted">Requirements have not been generated yet. Run Scope first, then click <strong>Run agent</strong> here.</p>';
}

function renderCostOrPending(session) {
  const dl = $('#downloadBtn');
  if (session.cost && dl) {
    dl.hidden = false;
    dl.href = `/api/sessions/${session.sessionId}/workbook`;
    dl.setAttribute('download', '');
  } else if (dl) {
    dl.hidden = true;
  }
  if (session.cost && (session.cost.lineItems || []).length) {
    renderCost(session.cost);
    return;
  }
  const totals = $('#totals'); if (totals) totals.innerHTML = '';
  $('#tab-cost').innerHTML = '<p class="muted">Cost Model has not been generated yet. Run Scope first, then click <strong>Run agent</strong> here.</p>';
}

function renderProjectOrPending(session) {
  const dl = $('#downloadBtn');
  if (session.projectCost && dl) {
    dl.hidden = false;
    dl.href = `/api/sessions/${session.sessionId}/workbook`;
    dl.setAttribute('download', '');
  } else if (dl) {
    dl.hidden = true;
  }
  if (session.projectCost && (session.projectCost.roles || []).length) {
    renderProjectCost(session.projectCost);
    return;
  }
  const totals = $('#projectTotals'); if (totals) totals.innerHTML = '';
  $('#tab-project').innerHTML = '<p class="muted">Project Cost has not been generated yet. Run Scope first, then click <strong>Run agent</strong> here.</p>';
}

function renderOperationsOrPending(session) {
  const dl = $('#downloadBtn');
  if (session.operations && dl) {
    dl.hidden = false;
    dl.href = `/api/sessions/${session.sessionId}/workbook`;
    dl.setAttribute('download', '');
  } else if (dl) {
    dl.hidden = true;
  }
  if (session.operations && (session.operations.items || []).length) {
    renderOperations(session.operations);
    return;
  }
  const totals = $('#operationsTotals'); if (totals) totals.innerHTML = '';
  $('#tab-operations').innerHTML = '<p class="muted">Operation Cost has not been generated yet. Run Scope first, then click <strong>Run agent</strong> here.</p>';
}

function renderCompareOrPending(session) {
  if (session.compare) {
    renderCompare(session.compare);
    return;
  }
  $('#compareBody').innerHTML = '<p class="muted">Comparison has not been generated yet. Run Cost Model, Project Cost, and Operation Cost first, then click <strong>Run comparison</strong>.</p>';
}

function renderOperations(o) {
  OPERATIONS_STATE = o;
  const items = o.items || [];
  items.forEach(i => { i.monthlyCost = Math.round(Number(i.quantity || 0) * Number(i.unitPrice || 0) * 100) / 100; });
  renderOperationsTotals(o);
  if (!items.length) { $('#tab-operations').innerHTML = '<p class="muted">No operating line items were generated for this estimation. <a href="/">Run the estimate again</a> to generate the operation (run) cost.</p>'; return; }
  const rows = items.map((i, idx) => `<tr>
      <td>${esc(i.category)}</td><td>${esc(i.item)}</td><td class="muted">${esc(i.description)}</td>
      <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="qty" value="${Number(i.quantity)}" aria-label="Quantity for ${esc(i.item)}" /></td>
      <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="unitPrice" value="${Number(i.unitPrice)}" aria-label="Unit price for ${esc(i.item)}" /></td>
      <td class="muted">${esc(i.unit)}</td>
      <td class="num-col" data-op="${idx}"><strong>${fmtMoney(i.monthlyCost, o.currency)}</strong></td></tr>`).join('');
  $('#tab-operations').innerHTML = `
    <table><thead><tr><th>Category</th><th>Item</th><th>Description</th>
      <th class="num-col">Qty</th><th class="num-col">Unit price</th><th>Unit</th><th class="num-col">Monthly</th></tr></thead>
    <tbody>${rows}</tbody>
    <tfoot><tr><th colspan="6" class="num-col">Monthly total (incl. <span>${o.contingencyPercent}</span>% contingency)</th>
      <th class="num-col" id="operationsFootTotal">${fmtMoney(operationsTotalWithContingency(o), o.currency)}</th></tr></tfoot></table>
    <p class="muted" style="margin-top:.7rem">${(o.notes || []).map(esc).join(' · ')}</p>`;
  $all('.qty-input', $('#tab-operations')).forEach(inp => inp.addEventListener('input', onOperationsEdit));
}

function operationsMonthlyRaw(o) {
  return (o.items || []).reduce((s, i) => s + Number(i.monthlyCost || 0), 0);
}

function operationsTotalWithContingency(o) {
  const pct = Number(o.contingencyPercent || 0);
  return Math.round(operationsMonthlyRaw(o) * (1 + pct / 100) * 100) / 100;
}

function onOperationsEdit(e) {
  const idx = Number(e.target.dataset.row);
  const field = e.target.dataset.field;
  const o = OPERATIONS_STATE;
  if (!o || !o.items[idx]) return;
  const val = Number(e.target.value);
  const safe = isFinite(val) && val >= 0 ? val : 0;
  const item = o.items[idx];
  if (field === 'qty') item.quantity = safe; else item.unitPrice = safe;
  item.monthlyCost = Math.round(Number(item.quantity || 0) * Number(item.unitPrice || 0) * 100) / 100;
  const cell = $(`[data-op="${idx}"]`); if (cell) cell.innerHTML = `<strong>${fmtMoney(item.monthlyCost, o.currency)}</strong>`;
  const foot = $('#operationsFootTotal'); if (foot) foot.textContent = fmtMoney(operationsTotalWithContingency(o), o.currency);
  renderOperationsTotals(o);
}

function renderOperationsTotals(o) {
  const el = $('#operationsTotals');
  if (!el) return;
  const pct = Number(o.contingencyPercent || 0);
  const raw = operationsMonthlyRaw(o);
  const monthly = Math.round(raw * (1 + pct / 100) * 100) / 100;
  el.innerHTML = `
    <div class="total-box hi"><div class="num">${fmtMoney(monthly, o.currency)}</div><div class="lbl">Run cost / mo</div></div>
    <div class="total-box"><div class="num">${fmtMoney(monthly * 12, o.currency)}</div><div class="lbl">Run cost / yr</div></div>
    <div class="total-box"><div class="num">${fmtMoney(raw, o.currency)}</div><div class="lbl">Monthly (excl. contingency)</div></div>
    <div class="total-box"><div class="num">${(o.items || []).length}</div><div class="lbl">Line items</div></div>
    <div class="total-box"><div class="num">${pct}%</div><div class="lbl">Contingency</div></div>`;
}

function renderScope(s) {
  const ul = (arr) => (arr && arr.length) ? '<ul class="tight">' + arr.map(x => `<li>${esc(x)}</li>`).join('') + '</ul>' : '<span class="muted">—</span>';
  $('#tab-scope').innerHTML = `
    <dl class="kv">
      <dt>Overview</dt><dd>${esc(s.overview)}</dd>
      <dt>Business goal</dt><dd>${esc(s.businessGoal)}</dd>
      <dt>Workload profile</dt><dd>${esc(s.workloadProfile)}</dd>
      <dt>Expected scale</dt><dd>${esc(s.expectedScale)}</dd>
      <dt>Data sensitivity</dt><dd>${esc(s.dataSensitivity)}</dd>
      <dt>Environment</dt><dd>${esc(s.environment)}</dd>
      <dt>In scope</dt><dd>${ul(s.inScope)}</dd>
      <dt>Out of scope</dt><dd>${ul(s.outOfScope)}</dd>
      <dt>Assumptions</dt><dd>${ul(s.assumptions)}</dd>
    </dl>`;
}

function renderRequirements(reqs) {
  if (!reqs.length) { $('#tab-requirements').innerHTML = '<p class="muted">No requirements.</p>'; return; }
  $('#tab-requirements').innerHTML = `
    <table><thead><tr><th>ID</th><th>Category</th><th>Priority</th><th>Requirement</th><th>Rationale</th></tr></thead>
    <tbody>${reqs.map(q => `<tr>
      <td>${esc(q.id)}</td><td>${esc(q.category)}</td>
      <td><span class="pill ${esc(q.priority)}">${esc(q.priority)}</span></td>
      <td>${esc(q.requirement)}</td><td class="muted">${esc(q.rationale)}</td></tr>`).join('')}</tbody></table>`;
}

// Editable cost model with non-prod / prod / total environment views. Qty cells are inputs; editing
// recalculates Monthly + totals live. Pricing reference links make each line auditable.
let COST_STATE = null;
let COST_ENV = 'total';   // 'nonprod' | 'prod' | 'total'

const ENV_LABEL = { nonprod: 'Non-Prod', prod: 'Prod', total: 'Total' };
const ENV_NOTE = {
  nonprod: 'Non-production (dev/test/POC) footprint — a scaled-down version of the same architecture.',
  prod: 'Production footprint — full sizing for the live workload.',
  total: 'Total cost of ownership — Non-Prod + Prod across all environments.'
};

function ensureLineDefaults(i) {
  // Backfill env fields for older stored jobs that predate the non-prod model.
  if (i.nonProdQuantity === undefined || i.nonProdQuantity === null) {
    i.nonProdQuantity = Math.round(Number(i.quantity || 0) * 0.4 * 10000) / 10000;
  }
  i.prodMonthlyCost = Math.round(Number(i.quantity || 0) * Number(i.unitPrice || 0) * 100) / 100;
  i.nonProdMonthlyCost = Math.round(Number(i.nonProdQuantity || 0) * Number(i.unitPrice || 0) * 100) / 100;
  i.totalMonthlyCost = Math.round((i.prodMonthlyCost + i.nonProdMonthlyCost) * 100) / 100;
}

function priceRefLink(i) {
  if (!i.pricingReferenceUrl) return '<span class="muted">—</span>';
  const label = i.pricingReferenceLabel || 'Azure pricing';
  return `<a class="price-ref" href="${esc(i.pricingReferenceUrl)}" target="_blank" rel="noopener noreferrer" title="${esc(i.pricingReferenceUrl)}">${esc(label)} ↗</a>`;
}

function renderCost(c) {
  COST_STATE = c;
  (c.lineItems || []).forEach(ensureLineDefaults);
  wireEnvToggle();
  renderCostTable();
}

function wireEnvToggle() {
  const toggle = $('#envToggle');
  if (!toggle || toggle.dataset.wired) return;
  $all('.env-btn', toggle).forEach(btn => btn.addEventListener('click', () => {
    COST_ENV = btn.dataset.env;
    $all('.env-btn', toggle).forEach(b => {
      const on = b === btn;
      b.classList.toggle('active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    renderCostTable();
  }));
  toggle.dataset.wired = '1';
}

function renderCostTable() {
  const c = COST_STATE;
  if (!c) return;
  const items = c.lineItems || [];
  const note = $('#envNote'); if (note) note.textContent = ENV_NOTE[COST_ENV] || '';
  if (!items.length) { $('#tab-cost').innerHTML = '<p class="muted">No cost items.</p>'; const t = $('#totals'); if (t) t.innerHTML = ''; return; }
  renderTotals(c);

  const head = (COST_ENV === 'total')
    ? `<tr><th>Category</th><th>Service</th><th>SKU</th><th>Pricing ref</th>
         <th class="num-col">Non-Prod Qty</th><th class="num-col">Prod Qty</th>
         <th class="num-col">Non-Prod</th><th class="num-col">Prod</th><th class="num-col">Total</th></tr>`
    : `<tr><th>Category</th><th>Service</th><th>SKU</th><th>Assumption</th><th>Pricing ref</th>
         <th class="num-col">Qty</th><th class="num-col">Unit price</th><th class="num-col">Monthly</th></tr>`;

  const rows = items.map((i, idx) => {
    if (COST_ENV === 'total') {
      return `<tr>
        <td>${esc(i.category)}</td><td>${esc(i.service)}</td><td>${esc(i.sku)}</td>
        <td>${priceRefLink(i)}</td>
        <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="nonprod" value="${Number(i.nonProdQuantity)}" aria-label="Non-prod quantity for ${esc(i.service)}" /></td>
        <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="prod" value="${Number(i.quantity)}" aria-label="Prod quantity for ${esc(i.service)}" /></td>
        <td class="num-col" data-np="${idx}">${fmtMoney(i.nonProdMonthlyCost, c.currency)}</td>
        <td class="num-col" data-pr="${idx}">${fmtMoney(i.prodMonthlyCost, c.currency)}</td>
        <td class="num-col" data-tot="${idx}"><strong>${fmtMoney(i.totalMonthlyCost, c.currency)}</strong></td></tr>`;
    }
    const qty = COST_ENV === 'nonprod' ? Number(i.nonProdQuantity) : Number(i.quantity);
    const monthly = COST_ENV === 'nonprod' ? i.nonProdMonthlyCost : i.prodMonthlyCost;
    const field = COST_ENV === 'nonprod' ? 'nonprod' : 'prod';
    return `<tr>
      <td>${esc(i.category)}</td><td>${esc(i.service)}</td><td>${esc(i.sku)}</td>
      <td class="muted">${esc(i.assumption)}</td>
      <td>${priceRefLink(i)}</td>
      <td class="num-col"><input class="qty-input" type="number" min="0" step="any" data-row="${idx}" data-field="${field}" value="${qty}" aria-label="Quantity for ${esc(i.service)}" /></td>
      <td class="num-col">${fmtMoney(i.unitPrice, c.currency)}</td>
      <td class="num-col" data-monthly="${idx}">${fmtMoney(monthly, c.currency)}</td></tr>`;
  }).join('');

  const footColspan = COST_ENV === 'total' ? 8 : 7;
  const footTotal = envTotalWithContingency(c);
  $('#tab-cost').innerHTML = `
    <table><thead>${head}</thead>
    <tbody>${rows}</tbody>
    <tfoot><tr><th colspan="${footColspan}" class="num-col">${ENV_LABEL[COST_ENV]} monthly total (incl. <span id="contPct">${c.contingencyPercent}</span>% contingency)</th>
      <th class="num-col" id="costFootTotal">${fmtMoney(footTotal, c.currency)}</th></tr></tfoot></table>
    <p class="muted" style="margin-top:.7rem">${(c.notes || []).map(esc).join(' · ')}</p>`;
  $all('.qty-input').forEach(inp => inp.addEventListener('input', onQtyEdit));
}

function envRawTotal(c) {
  const items = c.lineItems || [];
  if (COST_ENV === 'nonprod') return items.reduce((s, i) => s + Number(i.nonProdMonthlyCost || 0), 0);
  if (COST_ENV === 'prod') return items.reduce((s, i) => s + Number(i.prodMonthlyCost || 0), 0);
  return items.reduce((s, i) => s + Number(i.totalMonthlyCost || 0), 0);
}

function envTotalWithContingency(c) {
  const raw = envRawTotal(c);
  const pct = Number(c.contingencyPercent || 0);
  return Math.round(raw * (1 + pct / 100) * 100) / 100;
}

function onQtyEdit(e) {
  const idx = Number(e.target.dataset.row);
  const field = e.target.dataset.field;
  const c = COST_STATE;
  if (!c || !c.lineItems[idx]) return;
  const val = Number(e.target.value);
  const item = c.lineItems[idx];
  const safe = isFinite(val) && val >= 0 ? val : 0;
  if (field === 'nonprod') item.nonProdQuantity = safe; else item.quantity = safe;
  ensureLineDefaults(item);
  // Keep legacy field in sync for any code/store that still reads monthlyCost (= prod).
  item.monthlyCost = item.prodMonthlyCost;
  const npCell = $(`[data-np="${idx}"]`); if (npCell) npCell.textContent = fmtMoney(item.nonProdMonthlyCost, c.currency);
  const prCell = $(`[data-pr="${idx}"]`); if (prCell) prCell.innerHTML = fmtMoney(item.prodMonthlyCost, c.currency);
  const totCell = $(`[data-tot="${idx}"]`); if (totCell) totCell.innerHTML = `<strong>${fmtMoney(item.totalMonthlyCost, c.currency)}</strong>`;
  const mCell = $(`[data-monthly="${idx}"]`);
  if (mCell) mCell.textContent = fmtMoney(COST_ENV === 'nonprod' ? item.nonProdMonthlyCost : item.prodMonthlyCost, c.currency);
  recomputeTotals(c);
}

function recomputeTotals(c) {
  // Maintain prod headline fields (used elsewhere: context line, history, download summary).
  const prodRaw = (c.lineItems || []).reduce((sum, i) => sum + Number(i.prodMonthlyCost || 0), 0);
  const pct = Number(c.contingencyPercent || 0);
  c.monthlyTotal = Math.round(prodRaw * 100) / 100;
  c.monthlyTotalWithContingency = Math.round(prodRaw * (1 + pct / 100) * 100) / 100;
  c.annualTotal = Math.round(prodRaw * 12 * 100) / 100;
  const foot = $('#costFootTotal'); if (foot) foot.textContent = fmtMoney(envTotalWithContingency(c), c.currency);
  renderTotals(c);
}

function renderTotals(c) {
  const el = $('#totals');
  if (!el) return;
  const items = c.lineItems || [];
  const pct = Number(c.contingencyPercent || 0);
  const npRaw = items.reduce((s, i) => s + Number(i.nonProdMonthlyCost || 0), 0);
  const prRaw = items.reduce((s, i) => s + Number(i.prodMonthlyCost || 0), 0);
  const totRaw = Math.round((npRaw + prRaw) * 100) / 100;
  const withCont = (n) => Math.round(n * (1 + pct / 100) * 100) / 100;
  // Highlight the box for the currently selected environment view.
  const hi = (env) => COST_ENV === env ? ' hi' : '';
  el.innerHTML = `
    <div class="total-box${hi('nonprod')}"><div class="num">${fmtMoney(withCont(npRaw), c.currency)}</div><div class="lbl">Non-Prod / mo</div></div>
    <div class="total-box${hi('prod')}"><div class="num">${fmtMoney(withCont(prRaw), c.currency)}</div><div class="lbl">Prod / mo</div></div>
    <div class="total-box${hi('total')}"><div class="num">${fmtMoney(withCont(totRaw), c.currency)}</div><div class="lbl">Total / mo</div></div>
    <div class="total-box"><div class="num">${fmtMoney(withCont(totRaw) * 12, c.currency)}</div><div class="lbl">Total / yr</div></div>
    <div class="total-box"><div class="num">${pct}%</div><div class="lbl">Contingency</div></div>`;
}

function renderSteps(steps, stepStates) {
  const statusHtml = stepStates
    ? '<ul class="tight">' + Object.keys(stepStates).map(key => {
      const state = stepStates[key] || {};
      const lastRun = state.lastRunUtc ? new Date(state.lastRunUtc).toLocaleString() : 'Never';
      return `<li><strong>${esc(key)}:</strong> ${esc(state.status || 'pending')} <span class="muted">· ${esc(lastRun)}</span>${state.error ? ` — ${esc(state.error)}` : ''}</li>`;
    }).join('') + '</ul>'
    : '';
  const transcriptHtml = steps.length
    ? '<ul class="tight">' + steps.map(s => `<li><strong>${esc(s.step)}:</strong> ${esc(s.summary)}</li>`).join('') + '</ul>'
    : '<p class="muted">No steps recorded.</p>';
  $('#tab-steps').innerHTML = `${statusHtml ? `<h3>Step status</h3>${statusHtml}` : ''}<h3>Transcript</h3>${transcriptHtml}`;
}

// Step instruction cards on the Agent Steps page.
async function renderStepCards() {
  const host = $('#stepCards');
  if (!host) return;
  const data = await getAgentInstructions();
  if (!data) { host.innerHTML = '<p class="muted">Agent instructions unavailable.</p>'; return; }
  host.innerHTML = data.steps.map(s => `
    <div class="step-card">
      <div class="step-card-head"><span class="step-badge">${esc(s.title)}</span><span class="muted">${esc(s.agent)}</span></div>
      <p>${esc(s.goal)}</p>
      <button type="button" class="btn btn-secondary btn-sm" data-agent-step="${esc(s.key)}">View instructions</button>
    </div>`).join('');
  $all('[data-agent-step]', host).forEach(b => b.addEventListener('click', () => showAgentStep(b.dataset.agentStep)));
}

// ================================================================ COMPARE page (Build vs Buy)
async function initCompare() {
  const { mode, data } = await loadPlatformState(() => true);
  platformContext(mode, data);
  if (!showOrEmpty(data, '#compareCard')) return;
  wireRunButton('#runCompareBtn', mode, async () => {
    try {
      const session = await postSessionStep('compare', '#runCompareBtn', 'Running…');
      platformContext('session', session);
      renderSessionStepMeta(session, 'compare');
      renderCompareOrPending(session);
    } catch (err) {
      const error = $('#stepError'); if (error) { error.hidden = false; error.textContent = err.message; }
    }
  });
  if (mode === 'session') {
    renderSessionStepMeta(data, 'compare');
    renderCompareOrPending(data);
    return;
  }
  const btn = $('#runCompareBtn');
  if (btn) {
    btn.hidden = false;
    if (!btn.dataset.wiredLegacy) {
      btn.dataset.wiredLegacy = '1';
      btn.addEventListener('click', () => runCompare(data.jobId));
    }
  }
}

async function runCompare(jobId) {
  const body = $('#compareBody');
  const btn = $('#runCompareBtn');
  if (btn) btn.disabled = true;
  if (body) body.innerHTML = '<p class="muted">Running the Build-vs-Buy analysis…</p>';
  try {
    const r = await fetch('/api/estimations/' + encodeURIComponent(jobId) + '/compare', { method: 'POST' });
    const cmp = await r.json();
    if (!r.ok) { if (body) body.innerHTML = `<p class="muted">Comparison failed: ${esc(cmp.error || r.statusText)}</p>`; return; }
    renderCompare(cmp);
  } catch (err) {
    if (body) body.innerHTML = `<p class="muted">Comparison error: ${esc(err.message)}</p>`;
  } finally {
    if (btn) btn.disabled = false;
  }
}

function renderCompare(cmp) {
  const body = $('#compareBody');
  if (!body) return;
  const ccy = cmp.currency || 'USD';
  const t = cmp.totals || {};
  const recLabel = { build: 'Build on Azure', buy: 'Buy off-the-shelf', neutral: 'Neutral / cost-neutral' }[cmp.recommendation] || cmp.recommendation;
  const recClass = { build: 'rec-build', buy: 'rec-buy', neutral: 'rec-neutral' }[cmp.recommendation] || 'rec-neutral';

  const cheaperTag = (c) => c === 'build' ? '<span class="pill Must">Build cheaper</span>'
    : c === 'buy' ? '<span class="pill Could">Buy cheaper</span>' : '<span class="pill Should">—</span>';

  const rows = (cmp.sections || []).map(s => `
    <tr>
      <td><strong>${esc(s.section)}</strong><div class="muted cmp-detail">Build: ${esc(s.buildDetail)}</div><div class="muted cmp-detail">Buy: ${esc(s.buyDetail)}</div></td>
      <td>${esc(s.costType)}</td>
      <td class="num-col">${fmtMoney(s.buildCost, ccy)}</td>
      <td class="num-col">${fmtMoney(s.buyCost, ccy)}</td>
      <td class="num-col">${s.difference >= 0 ? '+' : '−'}${fmtMoney(Math.abs(s.difference), ccy)}</td>
      <td>${cheaperTag(s.cheaper)}</td>
    </tr>
    <tr class="cmp-reason-row"><td colspan="6" class="muted"><em>${esc(s.reasoning)}</em></td></tr>`).join('');

  const totalsGrid = `
    <div class="cmp-totals">
      <div class="cmp-col">
        <h4>🏗️ Build on Azure</h4>
        <div class="total-box"><div class="num">${fmtMoney(t.buildOneTime, ccy)}</div><div class="lbl">One-time build</div></div>
        <div class="total-box"><div class="num">${fmtMoney(t.buildAnnualRecurring, ccy)}</div><div class="lbl">Annual run cost</div></div>
        <div class="total-box"><div class="num">${fmtMoney(t.buildYearOne, ccy)}</div><div class="lbl">Year 1 total</div></div>
        <div class="total-box hi"><div class="num">${fmtMoney(t.buildThreeYearTco, ccy)}</div><div class="lbl">3-year TCO</div></div>
      </div>
      <div class="cmp-col">
        <h4>🛒 Buy off-the-shelf</h4>
        <div class="total-box"><div class="num">${fmtMoney(t.buyOneTime, ccy)}</div><div class="lbl">One-time buy</div></div>
        <div class="total-box"><div class="num">${fmtMoney(t.buyAnnualRecurring, ccy)}</div><div class="lbl">Annual run cost</div></div>
        <div class="total-box"><div class="num">${fmtMoney(t.buyYearOne, ccy)}</div><div class="lbl">Year 1 total</div></div>
        <div class="total-box hi"><div class="num">${fmtMoney(t.buyThreeYearTco, ccy)}</div><div class="lbl">3-year TCO</div></div>
      </div>
    </div>`;

  const reasoning = (cmp.reasoning && cmp.reasoning.length)
    ? '<ul class="tight">' + cmp.reasoning.map(x => `<li>${esc(x)}</li>`).join('') + '</ul>'
    : '<p class="muted">No reasoning provided.</p>';

  const buyWarn = cmp.buyCostAvailable ? '' :
    '<p class="status error" style="display:block">No off-the-shelf “buy” cost section was found in the source documents, so only the build cost is shown. Add a COTS/SaaS price list to the brief for a full comparison.</p>';

  body.innerHTML = `
    ${buyWarn}
    <div class="cmp-recommend ${recClass}">
      <div class="cmp-rec-head"><span class="cmp-rec-badge">Recommendation</span><span class="cmp-rec-value">${esc(recLabel)}</span></div>
      <p class="cmp-rec-summary">${esc(cmp.summary)}</p>
    </div>
    ${totalsGrid}
    <h3 class="cmp-h">Cost by section</h3>
    <table class="cmp-table"><thead><tr>
      <th>Section</th><th>Type</th><th class="num-col">Build (${esc(ccy)})</th>
      <th class="num-col">Buy (${esc(ccy)})</th><th class="num-col">Buy − Build</th><th>Cheaper</th></tr></thead>
      <tbody>${rows}</tbody></table>
    <h3 class="cmp-h">Reasoning</h3>
    ${reasoning}
    <p class="muted" style="margin-top:.7rem">${(cmp.notes || []).map(esc).join(' · ')}</p>`;
}

// ================================================================ ESTIMATIONS page
function initEstimations() { loadHistory(); }

async function loadHistory() {
  const el = $('#history');
  if (!el) return;
  try {
    const r = await fetch('/api/estimations');
    const items = await r.json();
    if (!items.length) { el.innerHTML = '<p class="muted">No estimations yet. <a href="/">Run one →</a></p>'; return; }
    el.innerHTML = `<table><thead><tr><th>Project</th><th>Docs</th><th>Reqs</th>
      <th class="num-col">Monthly</th><th>Created</th><th></th><th></th></tr></thead><tbody>${items.map(i => `<tr>
      <td>${esc(i.project)}</td>
      <td>${i.documents}</td><td>${i.requirements}</td>
      <td class="num-col">${fmtMoney(i.monthlyTotal, i.currency)}</td>
      <td class="muted">${new Date(i.createdUtc).toLocaleString()}</td>
      <td><button type="button" class="btn btn-secondary btn-sm" data-load="${esc(i.jobId)}">Open</button></td>
      <td><a href="/api/estimations/${esc(i.jobId)}/workbook">⬇ xlsx</a></td></tr>`).join('')}</tbody></table>`;
    $all('[data-load]', el).forEach(b => b.addEventListener('click', () => loadJobIntoPlatform(b.dataset.load)));
  } catch {
    el.innerHTML = '<p class="muted">Could not load history.</p>';
  }
}

async function loadJobIntoPlatform(jobId) {
  try {
    const r = await fetch('/api/estimations/' + encodeURIComponent(jobId));
    if (!r.ok) return;
    const job = await r.json();
    Store.setJob(job);
    Store.clearSession();
    window.location.href = '/platform/scope';
  } catch { /* ignore */ }
}

// ================================================================ Agent-instruction popups
function wireAgentStepButtons() {
  $all('[data-agent-step]').forEach(b => {
    // Skip ones inside dynamically-rendered step cards (wired on render).
    if (b.closest('#stepCards')) return;
    b.addEventListener('click', () => showAgentStep(b.dataset.agentStep));
  });
}

async function getAgentInstructions() {
  if (AGENT_INSTRUCTIONS) return AGENT_INSTRUCTIONS;
  try {
    const r = await fetch('/api/agent-instructions');
    AGENT_INSTRUCTIONS = await r.json();
  } catch { AGENT_INSTRUCTIONS = null; }
  return AGENT_INSTRUCTIONS;
}

async function showAgentStep(key) {
  openModal('Agent instructions', '<p class="muted">Loading…</p>');
  const data = await getAgentInstructions();
  if (!data) { setModalBody('<p class="muted">Agent instructions unavailable.</p>'); return; }
  const step = data.steps.find(s => s.key === key);
  if (!step) { setModalBody('<p class="muted">No instructions for this step.</p>'); return; }
  setModalTitle(`${step.title} — ${step.agent}`);
  setModalBody(`
    <p class="step-goal"><strong>Goal:</strong> ${esc(step.goal)}</p>
    <h4>Agent persona</h4>
    <pre class="doc-md">${esc(data.persona)}</pre>
    <h4>Step instructions</h4>
    <pre class="doc-md">${esc(step.instructions)}</pre>`);
}

// ================================================================ Modal
function wireModal() {
  const root = $('#modalRoot');
  if (!root) return;
  root.addEventListener('click', (e) => { if (e.target.dataset.close) closeModal(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });
}
function openModal(title, bodyHtml) { setModalTitle(title); setModalBody(bodyHtml); const r = $('#modalRoot'); if (r) r.hidden = false; }
function closeModal() { const r = $('#modalRoot'); if (r) r.hidden = true; }
function setModalTitle(t) { const e = $('#modalTitle'); if (e) e.textContent = t; }
function setModalBody(html) { const e = $('#modalBody'); if (e) e.innerHTML = html; }
