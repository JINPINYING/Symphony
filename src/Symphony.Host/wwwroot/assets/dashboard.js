const elements = {
  heroPanel: document.getElementById("hero-panel"),
  alert: document.getElementById("dashboard-alert"),
  workflowEditor: document.getElementById("workflow-editor"),
  metricGrid: document.getElementById("metric-grid"),
  liveRuns: document.getElementById("live-runs"),
  activityFeed: document.getElementById("activity-feed"),
  instanceStatus: document.getElementById("instance-status"),
  rateLimits: document.getElementById("rate-limits")
};

const toneClasses = {
  healthy: "border-emerald-400/30 bg-emerald-400/10 text-emerald-100",
  warning: "border-amber-400/30 bg-amber-400/10 text-amber-100",
  danger: "border-rose-400/30 bg-rose-400/10 text-rose-100",
  info: "border-cyan-400/30 bg-cyan-400/10 text-cyan-100",
  neutral: "border-white/10 bg-white/5 text-slate-200"
};

const state = {
  runtime: null,
  snapshot: null,
  health: null,
  issueDetail: null,
  selectedIssue: null,
  workflowDocument: null,
  workflowDraft: null,
  workflowEditorExpanded: false,
  workflowDirty: false,
  workflowSaving: false,
  workflowError: null,
  workflowNotice: null,
  loading: false,
  refreshQueued: false,
  autoRefresh: true,
  showRawEvents: false,
  error: null,
  issueError: null,
  lastLoadedAt: null
};

const refreshIntervalMs = 15000;
let refreshHandle = null;
const baseDocumentTitle = document.title;

document.addEventListener("click", async event => {
  if (event.target.closest("[data-action='reload-workflow']")) {
    await reloadWorkflowEditor();
    return;
  }

  if (event.target.closest("[data-action='toggle-workflow-editor']")) {
    state.workflowEditorExpanded = !state.workflowEditorExpanded;
    renderWorkflowEditorSection(true);
    return;
  }

  if (event.target.closest("[data-action='save-workflow']")) {
    await saveWorkflowEditor();
    return;
  }

  if (event.target.closest("[data-action='toggle-raw-events']")) {
    state.showRawEvents = !state.showRawEvents;
    // loadDashboard re-fetches /api/v1/state, which now carries ?raw=true when
    // the flag is set. Deliberately NOT queueRefresh: this is a view change, not
    // a control action, and it must not poke the engine.
    await loadDashboard();
    return;
  }

  if (event.target.closest("[data-action='refresh']")) {
    await loadDashboard({ queueRefresh: true });
  }
});

document.addEventListener("input", event => {
  const field = event.target.closest("[data-workflow-field]");
  if (!field?.dataset.workflowField || !state.workflowDraft) {
    return;
  }

  state.workflowDraft[field.dataset.workflowField] = field.value;
  state.workflowDirty = true;
  state.workflowNotice = null;
  state.workflowError = null;
  syncWorkflowEditorChrome();
});

void loadDashboard();
scheduleRefresh();
watchForFrozenTab();

/* A background tab is not a paused tab - it is a LYING tab.
 *
 * Edge sleeps inactive tabs by default and browsers throttle background timers
 * hard, so the 15-second poll simply stops. The page then keeps displaying its
 * last render, including the words "updated now", because that label is drawn
 * from the last successful load rather than from the clock. A two-hour-old view
 * therefore reported an idle plane, confidently, while work was in flight - the
 * owner saw "the team is not doing anything" and the engine was mid-run.
 *
 * That is the worst version of a fault this page keeps having: a surface that
 * cannot tell "nothing is happening" from "I stopped looking". It is worse here
 * because the staleness stamp is part of what freezes.
 *
 * Two defences, because either alone leaves a hole:
 *   - refetch the moment the tab is looked at again, so a woken tab corrects
 *     itself before it can be believed;
 *   - independently, notice when the data on screen has aged past the poll
 *     interval by a wide margin and say so, which also covers a tab that is
 *     visible but whose timer was throttled or whose fetches are failing. */
function watchForFrozenTab() {
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") {
      void loadDashboard();
    }
  });
  window.addEventListener("pageshow", () => void loadDashboard());
  window.addEventListener("focus", () => void loadDashboard());

  // Cheap, and deliberately not the poll: this only repaints the staleness
  // banner, so it stays correct even when a fetch is what is broken.
  window.setInterval(renderStalenessBanner, 5000);
}

// How far past the poll interval the view may drift before it stops being
// trustworthy. Generous - a slow tick or one missed poll is not staleness - but
// far short of the hours a slept tab can sit at.
const staleAfterMs = 90000;

function viewAgeMs() {
  const stamp = state.snapshot?.generated_at || state.lastLoadedAt;
  if (!stamp) return null;
  const age = Date.now() - new Date(stamp).getTime();
  return Number.isNaN(age) ? null : age;
}

function renderStalenessBanner() {
  const banner = document.getElementById("staleness-banner");
  if (!banner) return;

  const age = viewAgeMs();
  if (age === null || age <= staleAfterMs) {
    banner.innerHTML = "";
    return;
  }

  banner.innerHTML =
    `<div class="stale-view">This view is ${escapeHtml(formatDurationFromMilliseconds(age))} old and is not updating. ` +
    `The tab was probably asleep - browsers freeze background tabs. Nothing here can be trusted until it refreshes.` +
    `<button type="button" data-action="refresh" class="stale-refresh">Refresh now</button></div>`;
}


async function loadDashboard({ queueRefresh = false } = {}) {
  if (state.loading) {
    return;
  }

  state.loading = true;
  state.refreshQueued = queueRefresh;
  state.error = null;
  render();

  try {
    if (queueRefresh) {
      await fetch("/api/v1/refresh", { method: "POST" });
    }

    const [healthResult, runtimeResult, stateResult, workflowResult] = await Promise.allSettled([
      fetchHealth(),
      fetchJson("/api/v1/runtime"),
      fetchJson(state.showRawEvents ? "/api/v1/state?raw=true" : "/api/v1/state"),
      fetchJson("/api/v1/workflow")
    ]);

    if (runtimeResult.status !== "fulfilled") {
      throw runtimeResult.reason;
    }

    if (stateResult.status !== "fulfilled") {
      throw stateResult.reason;
    }

    state.health = healthResult.status === "fulfilled"
      ? healthResult.value
      : {
          ok: false,
          label: "Unreachable",
          detail: healthResult.reason instanceof Error ? healthResult.reason.message : "Health probe failed."
        };
    state.runtime = runtimeResult.value;
    state.snapshot = stateResult.value;
    state.lastLoadedAt = new Date().toISOString();

    if (workflowResult?.status === "fulfilled") {
      state.workflowDocument = workflowResult.value;
      if (!state.workflowDirty || !state.workflowDraft) {
        state.workflowDraft = cloneValue(workflowResult.value);
      }

      if (!state.workflowDirty) {
        state.workflowError = null;
      }
    } else if (!state.workflowDirty) {
      state.workflowDocument = null;
      state.workflowDraft = null;
      state.workflowError = workflowResult?.reason instanceof Error
        ? workflowResult.reason.message
        : "Workflow editor could not be loaded.";
    }

    // The issue-detail panel is gone, and with it a per-poll /api/v1/{issue} call
    // that existed only to fill it.
  } catch (error) {
    state.error = error instanceof Error ? error.message : "Dashboard data could not be loaded.";
  } finally {
    state.loading = false;
    state.refreshQueued = false;
    render();
  }
}

function render() {
  updateDocumentTitle();
  elements.heroPanel.innerHTML = renderAttention() + renderHeroPanel() + wrapPanel(renderStaff());
  elements.alert.innerHTML = renderAlert();
  renderWorkflowEditorSection();
  elements.metricGrid.innerHTML = renderMetricCards();
  elements.liveRuns.innerHTML = renderLiveRuns() + renderQueue();
  elements.activityFeed.innerHTML = renderActivityFeed();
  mountRoadmap();
  elements.instanceStatus.innerHTML = renderInstanceStatus();
  elements.rateLimits.innerHTML = renderRateLimits();
  renderStalenessBanner();

  const autoRefreshToggle = document.getElementById("auto-refresh");
  if (autoRefreshToggle) {
    autoRefreshToggle.checked = state.autoRefresh;
    autoRefreshToggle.onchange = event => {
      state.autoRefresh = event.target.checked;
      scheduleRefresh();
      render();
    };
  }
}

function renderWorkflowEditorSection(force = false) {
  const desiredMode = !state.workflowDraft
    ? "unavailable"
    : state.workflowEditorExpanded
      ? "expanded"
      : "collapsed";

  if (
    force ||
    !state.workflowDirty ||
    state.workflowSaving ||
    state.workflowError ||
    !elements.workflowEditor.innerHTML ||
    elements.workflowEditor.dataset.renderMode !== desiredMode
  ) {
    elements.workflowEditor.innerHTML = renderWorkflowEditor();
    elements.workflowEditor.dataset.renderMode = desiredMode;
    return;
  }

  syncWorkflowEditorChrome();
}

// The owner's half of the page: "does this need me?", answered before any detail.
// The engine computes this (OwnerAttentionSummary) so the live page and the
// published copy cannot disagree. Every state carries a WORD as well as a
// colour - colour alone is not an accessible signal.
// Project narrative, read from config/ROADMAP.md rather than hard-coded here,
// so it cannot drift from reality without someone editing the file.
// The workforce view: who is working, on what, for how long. This is the
// question an operator opens a dashboard to ask, so it sits directly under the
// summary and above every diagnostic.
function renderStaff() {
  const staff = state.snapshot?.staff || [];
  if (!staff.length) return "";

  /* "The team" is everyone who acts on this project, so a row has to say what
   * KIND of member it is - a runner the plane dispatches to, a scheduler that
   * wakes it, a session working beside the queue, or the owner the decisions land
   * on. Without that the rows are four different things wearing one costume. */
  const roleLabel = {
    runner: "Runner",
    scheduler: "Scheduler",
    session: "Session",
    owner: "You"
  };
  const stateLabel = { working: "Working", idle: "Idle", waiting: "Waiting", late: "Late" };

  const rows = staff.map(m => {
    const working = m.state === "working";
    const facts = [];
    if (working) {
      if (m.elapsed_seconds != null) facts.push(`${formatDurationFromMilliseconds(m.elapsed_seconds * 1000)} elapsed`);
      if (m.turn_count != null) facts.push(`${formatNumber(m.turn_count)} turn${m.turn_count === 1 ? "" : "s"}`);
      if (m.total_tokens) facts.push(`${formatNumber(m.total_tokens)} tokens`);
    } else if (m.elapsed_seconds != null) {
      facts.push(`${formatDurationFromMilliseconds(m.elapsed_seconds * 1000)} ago`);
    }

    return `
      <li class="staff-row ${working ? "staff-working" : "staff-idle"} ${m.state === "late" || m.state === "waiting" ? "staff-attention" : ""}">
        <span class="staff-state">${escapeHtml(stateLabel[m.state] || m.state)}</span>
        <div class="staff-main">
          <div class="staff-name">${escapeHtml(m.runner)}<span class="staff-role">${escapeHtml(roleLabel[m.role] || m.role || "")}</span></div>
          <div class="staff-activity">${escapeHtml(m.activity)}</div>
          ${m.last_message ? `<div class="staff-msg">${escapeHtml(m.last_message)}</div>` : ""}
        </div>
        <div class="staff-facts">${facts.map(f => `<span>${escapeHtml(f)}</span>`).join("")}</div>
      </li>`;
  }).join("");

  const busy = staff.filter(m => m.state === "working").length;
  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Right now</div>
          <h2 class="section-title">What the team is doing</h2>
        </div>
        <span class="glass-badge">${escapeHtml(busy ? `${busy} of ${staff.length} working` : "all idle")}</span>
      </div>
      <ul class="staff-list">${rows}</ul>
    </div>`;
}


// The staff panel has no slot in the original markup, so it is wrapped in the
// same shell the other panels use rather than styled separately.
function wrapPanel(inner) {
  return inner ? `<section class="panel mt-4">${inner}</section>` : "";
}

function roadmapRow(entry) {
  const word = entry.status === "done" ? "Done" : entry.status === "active" ? "Now" : "Planned";
  return `
    <li class="rm-row rm-${escapeHtml(entry.status)}">
      <span class="rm-status">${escapeHtml(word)}</span>
      ${entry.milestone ? `<span class="rm-ms">${escapeHtml(entry.milestone)}</span>` : ""}
      <span class="rm-title">${escapeHtml(entry.title)}</span>
    </li>`;
}

// "N of M done" as words, not a bar. The count is the honest summary of a task
// list and stays readable at a glance on a phone.
function roadmapTally(items) {
  const done = items.filter(e => e.status === "done").length;
  return `${done} of ${items.length} done`;
}

// The file can carry more than one project - the plane's own milestones and the
// product it is building - so entries are grouped in the order the file lists
// them. Ungrouped entries render as one flat list, exactly as before.
function roadmapGroups(items) {
  const groups = [];
  for (const entry of items) {
    const name = entry.group || "";
    const last = groups[groups.length - 1];
    if (last && last.name === name) last.items.push(entry);
    else groups.push({ name, items: [entry] });
  }
  return groups;
}

function renderRoadmap() {
  const items = state.snapshot?.roadmap || [];
  if (!items.length) return "";

  const groups = roadmapGroups(items);
  const grouped = groups.some(g => g.name);

  const body = grouped
    ? groups.map(g => `
        ${g.name ? `<div class="rm-group"><span class="rm-group-name">${escapeHtml(g.name)}</span><span class="rm-group-tally">${escapeHtml(roadmapTally(g.items))}</span></div>` : ""}
        <ul class="rm-list">${g.items.map(roadmapRow).join("")}</ul>`).join("")
    : `<ul class="rm-list">${items.map(roadmapRow).join("")}</ul>`;

  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Roadmap</div>
          <h2 class="section-title">Where this is going</h2>
        </div>
        ${grouped ? "" : `<span class="glass-badge">${escapeHtml(roadmapTally(items))}</span>`}
      </div>
      ${body}
    </div>`;
}


// The roadmap panel has no slot in the original markup, so it is appended once
// after the tracked-issues panel and re-rendered in place thereafter.
/* The roadmap has its own container in the markup now.
 *
 * It used to have none: it built one at runtime and anchored it after the
 * tracked-issues panel. When that panel was removed the anchor became null, the
 * function returned early, and the roadmap silently vanished from the page - the
 * exact failure this page keeps being fixed for, committed by the fix itself. A
 * panel that depends on an unrelated panel existing has a dependency nobody can
 * see in the markup, which is why it is now declared where it lives. */
function mountRoadmap() {
  const host = document.getElementById("roadmap-panel");
  if (!host) return;
  host.innerHTML = renderRoadmap() || "";
}

function renderAttention() {
  const a = state.snapshot?.attention;
  if (!a) return "";

  // Word first, colour second. The status has to be legible as text before any
  // colour registers - colour alone is not a signal everyone can read.
  const map = {
    clear:     { tone: "att-clear", word: "All clear",  mark: "&#10003;" },
    attention: { tone: "att-warn",  word: "Needs you",  mark: "!" },
    down:      { tone: "att-down",  word: "Blocked",    mark: "&#10005;" }
  };
  const m = map[a.level] || map.clear;

  // An item that names a decision but makes the reader go and find it is only
  // half an answer, so the label links straight to the thing when there is one.
  // http(s) only: the URL arrives as data, and a javascript: label would be a
  // scripting hole dressed up as a convenience.
  const items = (a.items || []).map(item => {
    const safeUrl = /^https?:\/\//i.test(item.url || "") ? item.url : null;
    const label = escapeHtml(item.label);
    return `
    <li class="att-item">
      <span class="att-sev ${item.severity === "down" ? "att-down" : "att-warn"}">${escapeHtml(item.severity === "down" ? "Blocking" : "Decide")}</span>
      <div>
        <div class="att-item-label">${safeUrl
          ? `<a class="att-item-link" href="${escapeHtml(safeUrl)}" target="_blank" rel="noopener noreferrer">${label}</a>`
          : label}</div>
        <div class="att-item-detail">${escapeHtml(item.detail)}</div>
      </div>
    </li>`;
  }).join("");

  // Work an agent did outside the queue. The counts above only ever described
  // dispatched runs, so without this the page can be busy and look asleep.
  //
  // Every row carries its own age now, and the strip says so when the newest
  // report has gone cold. This feed is written by agents POSTing into it, so it
  // reports nothing both when nothing happened and when nobody said anything -
  // and on 2026-09-01 it showed a day-old Stage 1 handoff as ambient context
  // while three repositories were being committed to. Undated rows under a
  // "nothing needs you" headline read as current. A feed that cannot distinguish
  // quiet from unreported must at least date what it is showing.
  /* Rows stored before the endpoint enforced this - a bare "a", the word "test",
   * two hundred consecutive x's - sat above real work in the panel that answers
   * "what is the team doing". The endpoint refuses them now, so this only ever
   * applies to what is already in the log; the rows stay there for audit rather
   * than being deleted, and simply stop being presented as reports.
   *
   * The same three blunt rules as the server, deliberately: if the two ever
   * disagree, the page is showing something the engine would not have accepted. */
  const looksLikeAReport = summary => {
    const t = (summary || "").trim();
    if (t.length < 12) return false;
    if (!/\s/.test(t)) return false;
    return !t.split(/\s+/).some(word => word.length > 60);
  };

  const reports = (state.snapshot?.agent_activity || [])
    .filter(r => looksLikeAReport(r.summary))
    .slice(0, 4);
  const newest = reports.length
    ? Math.min(...reports.map(r => Date.now() - Date.parse(r.at)).filter(ms => !isNaN(ms)))
    : null;
  const STALE_AFTER_MS = 2 * 60 * 60 * 1000;

  /* "Now" replaced the age instead of decorating it, and it covered a fifteen
   * minute window - so a report from 14 minutes ago read "Now" while the one
   * below it read "16 minutes ago". A two-minute difference rendered as a jump
   * from nothing to sixteen, which made a correctly ordered list look shuffled.
   * The owner spotted it and asked whether the feed was even in time order.
   *
   * Every row carries its real age now; liveness is a dot beside it rather than
   * a substitute for the number. Same rule as the rest of this page: a label must
   * not stand in front of the fact it is describing. */
  const activity = reports.map(report => `
    <li class="agent-row">
      <span class="agent-when">${report.live ? '<span class="agent-live" title="Reported within the last 15 minutes"></span>' : ""}${escapeHtml(formatRelativeTime(report.at))}</span>
      <span class="agent-actor">${escapeHtml(report.actor || "agent")}</span>
      <span class="agent-summary">${escapeHtml(report.summary || "")}</span>
    </li>`).join("");

  const activityNote = newest !== null && newest > STALE_AFTER_MS
    ? `<div class="agent-stale">Nothing reported for ${escapeHtml(formatDurationFromMilliseconds(newest))}. Agents report into this themselves, so an empty feed means no one has said anything - not that nothing is happening.</div>`
    : "";

  return `
    <div class="attention ${m.tone}">
      <div class="att-status"><span class="att-mark">${m.mark}</span><span class="att-word">${escapeHtml(m.word)}</span></div>
      <h1 class="att-headline">${escapeHtml(a.headline)}</h1>
      <p class="att-detail">${escapeHtml(a.detail)}</p>
      ${items ? `<ul class="att-list">${items}</ul>` : ""}
      ${activity ? `<div class="agent-strip"><div class="agent-kicker">Agent activity</div><ul class="agent-list">${activity}</ul>${activityNote}</div>` : ""}
    </div>`;
}

function renderHeroPanel() {
  const workflow = state.runtime?.workflow;
  const counts = state.snapshot?.counts;
  const refreshLabel = state.refreshQueued ? "Queuing tick..." : state.loading ? "Syncing..." : "Refresh now";

  // Everything here is metadata and controls. It used to carry a 5xl product
  // name above the actual answer, which read as decoration outranking content.
  return `
    <div class="metastrip">
      <div class="ms-line">
        <span class="ms-name">Watchtower</span>
        <span class="status-chip ${getHealthTone()}">${escapeHtml(state.health?.label || (state.loading ? "Syncing" : "Unknown"))}</span>
        <span class="ms-sep"></span>
        <span class="ms-fact" title="${escapeHtml((state.snapshot?.tracked_repositories || []).join(", "))}"><b>${escapeHtml(trackedRepositoriesLabel() || "no repository configured")}</b></span>
        <span class="ms-fact">${counts ? `${escapeHtml(formatNumber(counts.running))} active &middot; ${escapeHtml(formatNumber(counts.retrying))} retrying &middot; ${escapeHtml(formatNumber(counts.tracked))} tracked` : "waiting for telemetry"}</span>
        <span class="ms-fact">polls every ${escapeHtml(formatDurationFromMilliseconds(workflow?.polling?.intervalMs || state.runtime?.runtimeDefaults?.polling?.intervalMs || 0))}</span>
        <span class="ms-fact">updated ${escapeHtml(formatRelativeTime(state.snapshot?.generated_at || state.lastLoadedAt))}</span>
      </div>
      <div class="ms-controls">
        <label class="ms-auto"><input id="auto-refresh" type="checkbox"><span>Auto refresh</span></label>
        <button type="button" data-action="refresh" class="ms-refresh" ${state.loading ? "disabled" : ""}>${escapeHtml(refreshLabel)}</button>
      </div>
    </div>`;
}


function renderAlert() {
  if (!state.error) {
    return "";
  }

  return `
    <div class="panel border-rose-400/25 bg-rose-500/10">
      <div class="panel-body flex items-start gap-3 px-5 py-4 text-sm text-rose-50">
        <span class="mt-0.5 inline-flex h-6 w-6 items-center justify-center rounded-full bg-rose-500/20 font-display text-base">!</span>
        <div>
          <div class="font-medium">Dashboard refresh failed</div>
          <div class="mt-1 text-rose-100/80">${escapeHtml(state.error)}</div>
        </div>
      </div>
    </div>`;
}

function renderWorkflowEditor() {
  const draft = state.workflowDraft;

  if (!draft) {
    return `
      <div class="panel-body p-6 sm:p-8">
        <div class="section-kicker">Workflow editor</div>
        <h2 class="section-title">WORKFLOW.md</h2>
        <div class="mt-6">
          ${renderEmptyState("Workflow editor unavailable.", state.workflowError || "The workflow document could not be loaded.")}
        </div>
      </div>`;
  }

  const toggleLabel = state.workflowEditorExpanded ? "Minimize editor" : "Expand editor";
  const summaryCards = `
    <div class="mt-6 grid gap-4 lg:grid-cols-3">
      <div class="workflow-summary-card">
        <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Editor state</div>
        <div class="mt-2 text-sm leading-6 text-slate-300">The workflow editor is minimized by default to keep the control room focused. Expand it when you want to adjust YAML settings or the prompt template.</div>
      </div>
      <div class="workflow-summary-card">
        <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Workflow file</div>
        <div class="mt-2 break-all text-sm text-slate-200">${escapeHtml(draft.sourcePath || "Unavailable")}</div>
      </div>
      <div class="workflow-summary-card">
        <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Last valid load</div>
        <div class="mt-2 text-sm text-slate-200">${escapeHtml(draft.loadedAtUtc ? formatRelativeTime(draft.loadedAtUtc) : "Validation pending")}</div>
      </div>
    </div>`;

  return `
    <div class="panel-body p-6 sm:p-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div class="section-kicker">Workflow editor</div>
          <h2 class="section-title">WORKFLOW.md</h2>
          <p class="mt-3 max-w-3xl text-sm leading-6 text-slate-300">
            Edit the YAML front matter and prompt template that define Symphony's repository workflow. Saving writes back to WORKFLOW.md and the host reloads the updated workflow on the next access.
          </p>
        </div>
        <div class="workflow-actions">
          <span class="glass-badge" data-workflow-status>${escapeHtml(getWorkflowEditorStatusLabel())}</span>
          <button
            type="button"
            data-action="toggle-workflow-editor"
            class="workflow-button"
            ${state.workflowSaving ? "disabled" : ""}>
            ${escapeHtml(toggleLabel)}
          </button>
          ${state.workflowEditorExpanded ? `
          <button
            type="button"
            data-action="reload-workflow"
            class="workflow-button"
            ${state.workflowSaving ? "disabled" : ""}>
            Reload file
          </button>
          <button
            type="button"
            data-action="save-workflow"
            class="workflow-button workflow-button-primary"
            ${state.workflowSaving ? "disabled" : ""}>
            ${escapeHtml(state.workflowSaving ? "Saving..." : "Save WORKFLOW.md")}
          </button>
          ` : ""}
        </div>
      </div>

      <div data-workflow-feedback class="mt-6">${renderWorkflowEditorFeedback(draft)}</div>

      ${!state.workflowEditorExpanded ? summaryCards : `
      <div class="mt-6 grid gap-6 xl:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
        <label class="workflow-field">
          <span class="workflow-label">Workflow YAML front matter</span>
          <span class="workflow-help">Edit the YAML only. Symphony adds the surrounding --- delimiters when it saves.</span>
          <textarea
            data-workflow-field="frontMatterText"
            class="workflow-textarea workflow-textarea-code"
            spellcheck="false"
            rows="22">${escapeHtml(draft.frontMatterText || "")}</textarea>
        </label>

        <label class="workflow-field">
          <span class="workflow-label">Prompt template</span>
          <span class="workflow-help">This markdown body is passed to the Codex worker for each issue run.</span>
          <textarea
            data-workflow-field="promptTemplate"
            class="workflow-textarea"
            rows="22">${escapeHtml(draft.promptTemplate || "")}</textarea>
        </label>
      </div>

      <div class="mt-6 grid gap-4 lg:grid-cols-3">
        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-4">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Workflow file</div>
          <div class="mt-2 break-all text-sm text-slate-200">${escapeHtml(draft.sourcePath || "Unavailable")}</div>
        </div>
        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-4">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Last valid load</div>
          <div class="mt-2 text-sm text-slate-200">${escapeHtml(draft.loadedAtUtc ? formatRelativeTime(draft.loadedAtUtc) : "Validation pending")}</div>
        </div>
        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-4">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Editor behavior</div>
          <div class="mt-2 text-sm leading-6 text-slate-300">Auto-refresh leaves this panel alone while you have unsaved edits, so the rest of the dashboard can continue updating without clobbering your draft.</div>
        </div>
      </div>
      `}
    </div>`;
}

function renderWorkflowEditorFeedback(draft) {
  const banners = [];

  if (state.workflowNotice) {
    banners.push(`<div class="workflow-banner workflow-banner-success">${escapeHtml(state.workflowNotice)}</div>`);
  }

  if (state.workflowError) {
    banners.push(`<div class="workflow-banner workflow-banner-error">${escapeHtml(state.workflowError)}</div>`);
  }

  if (draft?.validationError) {
    banners.push(`<div class="workflow-banner workflow-banner-warning">Current file validation: ${escapeHtml(draft.validationError.message)}</div>`);
  }

  if (draft?.hasMaskedTrackerApiKey) {
    banners.push(`<div class="workflow-banner workflow-banner-info">Inline tracker.api_key is masked as ${escapeHtml(draft.trackerApiKeyPlaceholder)}. Leave that placeholder unchanged to keep the current secret, or replace it with a new literal or $ENV_VAR reference.</div>`);
  }

  return banners.length ? `<div class="space-y-4">${banners.join("")}</div>` : "";
}

function renderMetricCards() {
  const snapshot = state.snapshot;
  const runtime = state.runtime;
  const maxConcurrent = runtime?.workflow?.agent?.maxConcurrentAgents || runtime?.runtimeDefaults?.agent?.maxConcurrentAgents || 0;
  const utilization = maxConcurrent > 0 && snapshot ? Math.round((snapshot.counts.running / maxConcurrent) * 100) : 0;
  /* Only what would change what the reader does next.
   *
   * Removed: "Tracked issues", a count of mostly-closed history that grows
   * forever and can never prompt an action; "Codex runtime", cumulative seconds
   * nobody decides anything on; and "Lease state", an internal coordination row
   * that is diagnostic rather than status. "Running agents" and "Retry queue"
   * both said again what the strip above already says, and a number repeated in
   * two places is one more place for them to disagree.
   *
   * Token spend stays because the owner set a per-issue budget of 10M and this is
   * the only place spend is visible at all. */
  const metrics = [
    ["Total tokens", formatNumber(snapshot?.codex_totals?.total_tokens || 0), `${formatNumber(snapshot?.codex_totals?.input_tokens || 0)} in / ${formatNumber(snapshot?.codex_totals?.output_tokens || 0)} out`]
  ];

  return metrics.map(([label, value, detail]) => `
    <article class="metric-card">
      <div class="panel-body">
        <div class="metric-label">${escapeHtml(label)}</div>
        <div class="metric-value">${escapeHtml(value)}</div>
        <div class="metric-detail">${escapeHtml(detail)}</div>
      </div>
    </article>`).join("");
}

/* What is lined up, and why it has not started.
 *
 * The page showed what was running and what was tracked, and nothing about the
 * space between - so an issue labelled and waiting looked the same as one nobody
 * had queued. The owner asked to see the queue.
 *
 * The reason matters as much as the list. "Waiting for a free slot" is patience;
 * "in the pipeline at wait for repair" is progress; an issue sitting labelled
 * while the plane refuses to claim it is a fault, and those three read
 * identically as a bare list of titles. */
function renderQueue() {
  const queue = state.snapshot?.queue || [];

  const body = queue.length
    ? `<ul class="queue-list">${queue.map((q, i) => `
        <li class="queue-row">
          <span class="queue-pos">${i + 1}</span>
          <div class="queue-main">
            <div class="queue-title">${q.url
              ? `<a href="${escapeHtml(q.url)}" target="_blank" rel="noreferrer">${escapeHtml(q.issue_identifier)}</a>`
              : escapeHtml(q.issue_identifier)} ${escapeHtml(q.title || "")}</div>
            <div class="queue-why">${escapeHtml(q.waiting_on || "")}</div>
          </div>
        </li>`).join("")}</ul>`
    : `<div class="queue-empty">Nothing is queued. Label an issue <span class="k">symphony-ready</span> and it appears here.</div>`;

  return wrapPanel(`
    <div class="panel-body p-6">
      <div class="section-kicker">Up next</div>
      <h2 class="section-title">Queue${queue.length ? ` <span class="queue-count">${queue.length}</span>` : ""}</h2>
      <div class="mt-4">${body}</div>
    </div>`);
}

function renderLiveRuns() {
  const running = state.snapshot?.running || [];
  const retrying = state.snapshot?.retrying || [];
  const maxTurns = state.runtime?.workflow?.agent?.maxTurns || 0;

  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Live workload</div>
          <h2 class="section-title">Runs in flight</h2>
        </div>
        <span class="glass-badge">${escapeHtml(`${running.length} active / ${retrying.length} queued`)}</span>
      </div>

      <div class="mt-6 space-y-4">
        ${running.length ? running.map(run => renderRunningCard(run, maxTurns)).join("") : renderEmptyState("No agents are running.", "As soon as the worker dispatches an eligible issue, it will appear here with turns, session, and token totals.")}
      </div>

      <div class="mt-8">
        <div class="flex items-center justify-between gap-4">
          <h3 class="text-sm font-semibold uppercase tracking-[0.24em] text-slate-300">Retry queue</h3>
          <span class="text-xs text-slate-400">${escapeHtml(retrying.length ? "Ordered by due time" : "Idle")}</span>
        </div>
        <div class="mt-4 space-y-3">
          ${retrying.length ? retrying.map(renderRetryRow).join("") : renderCompactEmpty("No retry backlog")}
        </div>
      </div>
    </div>`;
}

function renderRunningCard(run, maxTurns) {
  const progress = maxTurns > 0 ? Math.min(run.turn_count / maxTurns, 1) : 0;
  const width = `${Math.max(progress * 100, run.turn_count > 0 ? 8 : 0)}%`;

  return `
    <button type="button" data-issue-identifier="${escapeHtml(run.issue_identifier)}" class="issue-row ${run.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${toneClasses.info}">Running</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(run.issue_identifier)}</span>
          <span class="truncate text-sm text-slate-300">${escapeHtml(run.title || "Untitled issue")}</span>
        </div>
        <div class="mt-3 grid gap-3 sm:grid-cols-2">
          <div>
            <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Latest event</div>
            <div class="mt-1 text-sm text-slate-200">${escapeHtml(run.last_event || "waiting")}</div>
            <div class="mt-1 text-sm text-slate-400">${escapeHtml(run.last_message || "No message recorded")}</div>
          </div>
          <div>
            <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Session and state</div>
            <div class="mt-1 text-sm text-slate-200">${escapeHtml(run.session_id || "Session not started")} in ${escapeHtml(run.state || "Unknown")}</div>
            <div class="mt-1 text-sm text-slate-400">Started ${escapeHtml(formatRelativeTime(run.started_at))}</div>
          </div>
        </div>
        <div class="mt-4">
          <div class="flex items-center justify-between text-xs uppercase tracking-[0.22em] text-slate-400">
            <span>Turn progress</span>
            <span>${escapeHtml(maxTurns ? `${run.turn_count}/${maxTurns}` : `${run.turn_count} turns`)}</span>
          </div>
          <div class="mt-2 h-2.5 overflow-hidden rounded-full bg-white/8">
            <div class="h-full rounded-full bg-gradient-to-r from-cyan-300 via-emerald-300 to-orange-300" style="width: ${width};"></div>
          </div>
        </div>
      </div>

      <div class="shrink-0 text-right">
        <div class="font-display text-2xl font-semibold text-white">${escapeHtml(formatNumber(run.tokens?.total_tokens || 0))}</div>
        <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Tokens</div>
      </div>
    </button>`;
}

function renderRetryRow(retry) {
  return `
    <button type="button" data-issue-identifier="${escapeHtml(retry.issue_identifier)}" class="issue-row ${retry.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${toneClasses.warning}">Retry</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(retry.issue_identifier)}</span>
          <span class="truncate text-sm text-slate-300">${escapeHtml(retry.title || "Tracked issue")}</span>
        </div>
        <div class="mt-2 text-sm text-slate-300">${escapeHtml(retry.error || "Retry waiting for its due time")}</div>
      </div>
      <div class="shrink-0 text-right text-sm text-slate-300">
        <div>Attempt ${escapeHtml(String(retry.attempt || 0))}</div>
        <div class="mt-1 text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(formatRetryCountdown(retry.due_at))}</div>
      </div>
    </button>`;
}

function renderActivityFeed() {
  const activity = state.snapshot?.activity || [];
  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Recent activity</div>
          <h2 class="section-title">Event stream</h2>
        </div>
        <div class="flex items-center gap-3">
          <span class="glass-badge">${escapeHtml(formatNumber(activity.length))} ${state.showRawEvents ? "raw events" : "activities"}</span>
          <button type="button" data-action="toggle-raw-events" class="glass-badge hover:text-cyan-100" title="Raw events are always recorded; this only changes what is shown.">
            ${state.showRawEvents ? "Show activity" : "Show raw events"}
          </button>
        </div>
      </div>
      <div class="mt-6 space-y-3">
        ${activity.length ? activity.map(renderActivityEntry).join("") : renderEmptyState("No activity logged yet.", "Dispatches, phase changes, verdicts and merges appear here. Streaming protocol events are hidden by default — use Show raw events to see everything.")}
      </div>
    </div>`;
}

function renderActivityEntry(entry) {
  const tone = getEventTone(entry);
  return `
    <div class="rounded-3xl border ${tone} bg-white/[0.035] p-4">
      <div class="flex flex-wrap items-center gap-2">
        <span class="status-chip ${tone}">${escapeHtml(entry.label || entry.event)}</span>
        ${entry.repeat_count > 1 ? `<span class="glass-badge" title="${escapeHtml(String(entry.repeat_count))} consecutive identical events collapsed into one row">&times;${escapeHtml(String(entry.repeat_count))}</span>` : ""}
        ${entry.issue_identifier ? `<button type="button" data-issue-identifier="${escapeHtml(entry.issue_identifier)}" class="text-sm font-semibold text-white hover:text-cyan-100">${escapeHtml(entry.issue_identifier)}</button>` : ""}
        <span class="text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(formatRelativeTime(entry.at))}</span>
      </div>
      ${entry.message ? `<p class="mt-3 text-sm leading-6 text-slate-300">${escapeHtml(entry.message)}</p>` : ""}
      <div class="mt-3 flex flex-wrap gap-3 text-xs text-slate-400">
        ${entry.is_protocol ? `<span class="font-mono">${escapeHtml(entry.event)}</span>` : ""}
        ${entry.session_id ? `<span>Session ${escapeHtml(entry.session_id)}</span>` : ""}
        ${entry.level ? `<span>${escapeHtml(entry.level)}</span>` : ""}
      </div>
    </div>`;
}

function renderTrackedIssue(issue) {
  const trackedState = issue.state || "Unknown state";
  return `
    <button type="button" data-issue-identifier="${escapeHtml(issue.issue_identifier)}" class="issue-row ${issue.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${getIssueStatusTone(issue.status)}">${escapeHtml(issue.status || "tracked")}</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(issue.issue_identifier)}</span>
        </div>
        <div class="mt-2 text-sm text-slate-200">${escapeHtml(issue.title || "Untitled issue")}</div>
        <div class="mt-3 flex flex-wrap gap-2 text-xs text-slate-400">
          <span>${escapeHtml(trackedState)}</span>
          ${issue.milestone ? `<span>Milestone ${escapeHtml(issue.milestone)}</span>` : ""}
          <span>Updated ${escapeHtml(formatRelativeTime(issue.updated_at))}</span>
        </div>
      </div>
      <div class="shrink-0 self-center text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(trackedState)}</div>
    </button>`;
}

/* The schedulers that wake this plane.
 *
 * Nothing rendered these until one of them died and stayed dead for 27 hours
 * while the page reported everything as fine. The engine cannot tell the
 * difference from the inside - a scheduler that stopped firing and a quiet week
 * look identical - so the state now carries what the host says about them.
 *
 * Health is spelled out as a word, never colour alone. A healthy scheduler is
 * shown but not emphasised: the point is to make silence legible, not to fill
 * the page with cron chatter nobody reads. */
function renderWatchedTasks() {
  const tasks = state.snapshot?.watched_tasks || [];
  if (!tasks.length) {
    return "";
  }

  const tone = health => {
    switch (health) {
      case "ok": return toneClasses.healthy;
      case "late": return toneClasses.warning;
      case "disabled":
      case "failing": return toneClasses.danger;
      default: return toneClasses.neutral;
    }
  };

  const label = health => ({
    ok: "On schedule",
    late: "Late",
    failing: "Failing",
    disabled: "Disabled",
    unknown: "Unmonitored"
  }[health] || "Unknown");

  const rows = tasks.map(task => `
    <div class="border-t border-white/10 pt-3 first:border-t-0 first:pt-0">
      <div class="flex items-center justify-between gap-3">
        <div class="text-sm font-medium text-white">${escapeHtml(task.name)}</div>
        <span class="status-chip ${tone(task.health)}">${escapeHtml(label(task.health))}</span>
      </div>
      <div class="mt-1 text-sm text-slate-300">${escapeHtml(task.explanation || "")}</div>
      <div class="mt-1 text-xs text-slate-400">
        Last run ${escapeHtml(task.last_run ? formatRelativeTime(task.last_run) : "never")}
        &middot; next ${escapeHtml(task.next_run ? formatRelativeTime(task.next_run) : "not scheduled")}
      </div>
    </div>`).join("");

  return `
    <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
      <div class="text-xs uppercase tracking-[0.22em] text-slate-400">What wakes the plane</div>
      <div class="mt-3 space-y-3">${rows}</div>
    </div>`;
}

function renderInstanceStatus() {
  const runtime = state.runtime;
  const workflow = runtime?.workflow;
  const leases = state.snapshot?.coordination?.leases || [];
  const activeLeases = leases.filter(entry => !entry.is_expired);

  return `
    <div class="panel-body p-6">
      <div class="section-kicker">Instance health</div>
      <h2 class="section-title">Host and coordination</h2>

      <div class="mt-6 grid gap-4">
        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="flex items-center justify-between gap-4">
            <div class="text-sm font-medium text-white">Health</div>
            <span class="status-chip ${getHealthTone()}">${escapeHtml(state.health?.label || "Unknown")}</span>
          </div>
          <div class="mt-3 text-sm text-slate-300">${escapeHtml(state.health?.detail || "No health detail available.")}</div>
        </div>

        ${renderWatchedTasks()}

        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Orchestrator</div>
          <div class="mt-2 space-y-2 text-sm text-slate-300">
            <div>Version: ${escapeHtml(runtime?.application?.version || "unknown")}</div>
            <div>Instance: ${escapeHtml(runtime?.orchestration?.instanceId || "auto-generated")}</div>
            <div>Lease: ${escapeHtml(runtime?.orchestration?.leaseName || "poll-dispatch")}</div>
            <div>Lease TTL: ${escapeHtml(formatSeconds(runtime?.orchestration?.leaseTtlSeconds || 0))}</div>
            <div>HTTP port: ${escapeHtml(String(workflow?.server?.port || "configured externally"))}</div>
          </div>
        </div>

        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Persistence</div>
          <div class="mt-2 text-sm text-slate-300">${runtime?.persistence?.isConfigured ? "SQLite configured" : "Persistence is not configured"}</div>
          <div class="mt-4 text-xs uppercase tracking-[0.22em] text-slate-400">Lease rows</div>
          <div class="mt-3 space-y-3">
            ${leases.length ? leases.map(lease => `
                <div class="rounded-2xl border ${lease.is_expired ? toneClasses.warning : toneClasses.healthy} p-3">
                  <div class="flex items-center justify-between gap-4 text-sm">
                    <span class="font-medium text-white">${escapeHtml(lease.lease_name)}</span>
                    <span>${escapeHtml(lease.is_expired ? "Expired" : "Active")}</span>
                  </div>
                  <div class="mt-2 text-xs text-slate-300">${escapeHtml(lease.owner_instance_id)} updated ${escapeHtml(formatRelativeTime(lease.updated_at))}</div>
                </div>`).join("") : renderCompactEmpty("No lease data")}
          </div>
          <div class="mt-3 text-xs text-slate-400">${escapeHtml(activeLeases.length ? `${activeLeases.length} active coordination lease(s)` : "Coordination is idle or has not run yet.")}</div>
        </div>
      </div>
    </div>`;
}

function renderRateLimits() {
  const rows = flattenEntries(state.snapshot?.rate_limits);
  return `
    <div class="panel-body p-6">
      <div class="section-kicker">Provider telemetry</div>
      <h2 class="section-title">Rate limits</h2>
      <p class="mt-3 text-sm leading-6 text-slate-300">Latest rate limit payload recorded from Codex app-server updates.</p>
      <div class="mt-6 space-y-3">
        ${rows.length ? rows.map(row => `
            <div class="rounded-2xl border border-white/8 bg-white/[0.035] px-4 py-3">
              <div class="text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(row.key)}</div>
              <div class="mt-1 break-all text-sm text-slate-200">${escapeHtml(row.value)}</div>
            </div>`).join("") : renderEmptyState("No rate-limit payload captured.", "Once Codex reports provider limits, the latest payload will be surfaced here for capacity debugging.")}
      </div>
    </div>`;
}

function renderEmptyState(title, description) {
  return `
    <div class="rounded-3xl border border-dashed border-white/10 bg-white/[0.03] p-5 text-sm text-slate-300">
      <div class="font-medium text-white">${escapeHtml(title)}</div>
      <div class="mt-2 leading-6 text-slate-400">${escapeHtml(description)}</div>
    </div>`;
}

function renderCompactEmpty(message) {
  return `<div class="rounded-2xl border border-dashed border-white/10 bg-white/[0.02] px-4 py-3 text-sm text-slate-400">${escapeHtml(message)}</div>`;
}

// The tab and the meta strip both named a single repository, left over from
// when there could only be one. The tracker watches several now, so naming the
// first of them is not shorthand - it is wrong, and it hides the others.
function trackedRepositoriesLabel() {
  const tracked = state.snapshot?.tracked_repositories || [];
  if (tracked.length > 1) {
    return `${tracked.length} repositories`;
  }

  const single = tracked[0];
  if (single) {
    return single;
  }

  const owner = state.runtime?.workflow?.tracker?.owner;
  const repo = state.runtime?.workflow?.tracker?.repo;
  return [owner, repo].filter(Boolean).join("/");
}

function updateDocumentTitle() {
  const label = trackedRepositoriesLabel();
  document.title = label ? `${label} | ${baseDocumentTitle}` : baseDocumentTitle;
}

function activeLeaseCount(snapshot) {
  return (snapshot?.coordination?.leases || []).filter(entry => !entry.is_expired).length;
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, { cache: "no-store", ...options });
  const contentType = response.headers.get("content-type") || "";
  const payload = response.status === 204
    ? null
    : contentType.includes("application/json")
      ? await response.json()
      : await response.text();

  if (!response.ok) {
    const errorMessage = payload && typeof payload === "object"
      ? payload.error?.message || payload.title
      : null;
    throw new Error(errorMessage || `Request to ${url} failed with ${response.status}.`);
  }

  return payload;
}

async function fetchHealth() {
  const response = await fetch("/api/v1/health", { cache: "no-store" });
  const detail = (await response.text()).trim();
  return {
    ok: response.ok,
    label: response.ok ? "Healthy" : "Degraded",
    detail: detail || (response.ok ? "Health checks passed." : `Health endpoint returned ${response.status}.`)
  };
}

function scheduleRefresh() {
  if (refreshHandle) {
    window.clearInterval(refreshHandle);
    refreshHandle = null;
  }

  if (state.autoRefresh) {
    refreshHandle = window.setInterval(() => {
      void loadDashboard();
    }, refreshIntervalMs);
  }
}

function getHealthTone() {
  if (!state.health) {
    return state.loading ? toneClasses.info : toneClasses.neutral;
  }

  return state.health.ok ? toneClasses.healthy : toneClasses.danger;
}

function getIssueStatusTone(status) {
  switch ((status || "").toLowerCase()) {
    case "running":
      return toneClasses.info;
    case "retrying":
      return toneClasses.warning;
    case "completed":
    case "succeeded":
      return toneClasses.healthy;
    case "failed":
      return toneClasses.danger;
    default:
      return toneClasses.neutral;
  }
}

function getEventTone(entry) {
  const value = `${entry?.event || ""} ${entry?.level || ""}`.toLowerCase();
  if (value.includes("fail") || value.includes("error") || value.includes("cancel")) return toneClasses.danger;
  if (value.includes("retry") || value.includes("warning")) return toneClasses.warning;
  if (value.includes("complete") || value.includes("closed") || value.includes("success")) return toneClasses.healthy;
  if (value.includes("dispatch") || value.includes("turn") || value.includes("notification")) return toneClasses.info;
  return toneClasses.neutral;
}

function flattenEntries(value, prefix = "") {
  if (value === null || value === undefined) return [];
  if (Array.isArray(value)) {
    return value.length
      ? value.flatMap((entry, index) => flattenEntries(entry, `${prefix}[${index}]`))
      : [{ key: prefix || "value", value: "[]" }];
  }

  if (typeof value === "object") {
    return Object.entries(value).flatMap(([key, nestedValue]) =>
      flattenEntries(nestedValue, prefix ? `${prefix}.${key}` : key));
  }

  return [{ key: prefix || "value", value: String(value) }];
}

function formatNumber(value) {
  return new Intl.NumberFormat().format(Number(value || 0));
}

function formatSeconds(value) {
  const seconds = Number(value || 0);
  if (seconds < 60) return `${seconds.toFixed(seconds >= 10 ? 0 : 1)}s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(minutes >= 10 ? 0 : 1)}m`;
  const hours = minutes / 60;
  return `${hours.toFixed(hours >= 10 ? 0 : 1)}h`;
}

function formatDurationFromMilliseconds(value) {
  return formatSeconds(Number(value || 0) / 1000);
}

function getRelativeDiffSeconds(value) {
  if (!value) return null;
  const timestamp = new Date(value).getTime();
  if (Number.isNaN(timestamp)) return null;
  return Math.round((timestamp - Date.now()) / 1000);
}

function formatRelativeDiff(diffSeconds) {
  const absoluteSeconds = Math.abs(diffSeconds);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  if (absoluteSeconds < 60) return formatter.format(diffSeconds, "second");
  const diffMinutes = Math.round(diffSeconds / 60);
  if (Math.abs(diffMinutes) < 60) return formatter.format(diffMinutes, "minute");
  const diffHours = Math.round(diffMinutes / 60);
  if (Math.abs(diffHours) < 48) return formatter.format(diffHours, "hour");
  return formatter.format(Math.round(diffHours / 24), "day");
}

function formatRelativeTime(value) {
  const diffSeconds = getRelativeDiffSeconds(value);
  if (diffSeconds === null) return "unavailable";
  return formatRelativeDiff(diffSeconds);
}

function formatRetryCountdown(value) {
  const diffSeconds = getRelativeDiffSeconds(value);
  if (diffSeconds === null) return "unavailable";
  return formatRelativeDiff(Math.max(diffSeconds, 0));
}

function getWorkflowEditorStatusLabel() {
  return state.workflowSaving
    ? "Saving"
    : state.workflowDirty
      ? "Unsaved edits"
      : "In sync";
}

function syncWorkflowEditorChrome() {
  const status = elements.workflowEditor.querySelector("[data-workflow-status]");
  if (status) {
    status.textContent = getWorkflowEditorStatusLabel();
  }

  const feedback = elements.workflowEditor.querySelector("[data-workflow-feedback]");
  if (feedback) {
    feedback.innerHTML = renderWorkflowEditorFeedback(state.workflowDraft);
  }
}

async function reloadWorkflowEditor() {
  if (state.workflowSaving) {
    return;
  }

  try {
    state.workflowError = null;
    state.workflowNotice = null;
    const [workflowDocument, runtime] = await Promise.all([
      fetchJson("/api/v1/workflow"),
      fetchJson("/api/v1/runtime")
    ]);
    state.workflowDocument = workflowDocument;
    state.workflowDraft = cloneValue(workflowDocument);
    state.workflowDirty = false;
    state.runtime = runtime;
  } catch (error) {
    state.workflowError = error instanceof Error ? error.message : "Workflow editor could not be reloaded.";
  }

  render();
}

async function saveWorkflowEditor() {
  if (!state.workflowDraft || state.workflowSaving) {
    return;
  }

  state.workflowSaving = true;
  state.workflowError = null;
  state.workflowNotice = null;
  render();

  try {
    const savedWorkflow = await fetchJson("/api/v1/workflow", {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(state.workflowDraft)
    });

    state.workflowDocument = savedWorkflow;
    state.workflowDraft = cloneValue(savedWorkflow);
    state.workflowDirty = false;
    state.runtime = await fetchJson("/api/v1/runtime");
    state.lastLoadedAt = new Date().toISOString();
    state.workflowNotice = "WORKFLOW.md saved successfully. Updated runtime settings are now live on the next workflow access.";
  } catch (error) {
    state.workflowError = error instanceof Error ? error.message : "WORKFLOW.md could not be saved.";
  } finally {
    state.workflowSaving = false;
    render();
  }
}

function cloneValue(value) {
  return value === null || value === undefined
    ? value
    : JSON.parse(JSON.stringify(value));
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value);
}
