// Panels are looked up per render rather than cached once: the advanced section
// creates and destroys the workflow editor as it is opened and closed, so a
// reference captured at load would go stale.
const elements = {
  get alert() { return document.getElementById("dashboard-alert"); },
  get workflowEditor() { return document.getElementById("workflow-editor"); }
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
  // Sections the reader has opened. Held in state rather than in the DOM because
  // the page re-renders every 15 seconds, which would slam a <details> shut mid-read.
  expanded: { roadmap: false, activity: false, queue: false, advanced: false },
  expandedProjects: {},
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

  const directiveButton = event.target.closest("[data-action='post-directive']");
  if (directiveButton?.dataset.issueId) {
    const button = directiveButton;
    if (button.disabled) return;

    const original = button.textContent.trim();
    button.disabled = true;
    button.textContent = "Posting…";

    const payload = {
      issueId: button.dataset.issueId,
      issueIdentifier: button.dataset.issueIdentifier || null,
      repository: button.dataset.repository || null,
      action: button.dataset.directiveAction || "resume",
      phase: button.dataset.directivePhase || null
    };

    /* Report what happened, including failure. A button that silently does
       nothing is the fault this whole panel exists to stop making. */
    fetch("/api/v1/actions/directive", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    }).then(async response => {
      if (response.ok) {
        button.textContent = "Posted";
        loadDashboard();
        return;
      }
      const detail = await response.json().catch(() => null);
      button.textContent = detail?.error?.message
        ? `Failed: ${String(detail.error.message).slice(0, 60)}`
        : `Failed (${response.status})`;
      button.disabled = false;
    }).catch(error => {
      button.textContent = `Failed: ${error?.message || "no answer from the plane"}`;
      button.disabled = false;
    });

    window.setTimeout(() => { button.textContent = original; button.disabled = false; }, 6000);
    return;
  }

  const copyCommand = event.target.closest("[data-action='copy-command']");
  if (copyCommand?.dataset.command) {
    const command = copyCommand.dataset.command;
    const say = text => {
      const original = copyCommand.dataset.originalLabel || copyCommand.textContent.trim();
      copyCommand.dataset.originalLabel = original;
      copyCommand.textContent = text;
      window.setTimeout(() => { copyCommand.textContent = original; }, 1600);
    };
    /* Clipboard access can be refused, and a button that silently does nothing is
       worse than one that admits it - so a refusal shows the command instead. */
    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(command).then(() => say("Copied"), () => say(command));
    } else {
      say(command);
    }
    return;
  }

  const projectToggle = event.target.closest("[data-action='toggle-project']");
  if (projectToggle?.dataset.project) {
    const key = projectToggle.dataset.project;
    state.expandedProjects[key] = !state.expandedProjects[key];
    render();
    return;
  }

  const sectionToggle = event.target.closest("[data-action='toggle-section']");
  if (sectionToggle?.dataset.section) {
    state.expanded[sectionToggle.dataset.section] = !state.expanded[sectionToggle.dataset.section];
    render();
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

/* Bootstrap after the module finishes evaluating. The render layer below
   declares WT_ICONS and friends with `const`, and calling render() from here
   would reach them inside their temporal dead zone - the page came up blank
   with "cannot access before initialization" until this was deferred. */
queueMicrotask(() => {
  void loadDashboard();
  scheduleRefresh();
  watchForFrozenTab();
});

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

// Panels in the order the reader needs them, not the order they were built in.
function render() {
  updateDocumentTitle();
  renderHeaderRight();
  renderLiveBadge();
  renderStalenessBanner();

  renderAttentionPanel();   // 1. does this need me
  renderHealthPanel();      // 2. is the machinery healthy
  renderTeamPanel();        // 3. what is the team doing
  renderQueuePanel();       // 4. what is queued or blocked, and why
  renderActivityPanel();    // 5. what just happened
  renderRoadmapPanel();     // 6. how are the projects progressing

  renderUtilityStrip();
  renderAdvancedPanel();
  if (elements.workflowEditor) {
    renderWorkflowEditorSection(true);
  }

  const alertHost = elements.alert;
  if (alertHost) alertHost.innerHTML = renderAlert();

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

  const host = elements.workflowEditor;
  if (!host) return;

  if (
    force ||
    !state.workflowDirty ||
    state.workflowSaving ||
    state.workflowError ||
    !host.innerHTML ||
    host.dataset.renderMode !== desiredMode
  ) {
    host.innerHTML = renderWorkflowEditor();
    host.dataset.renderMode = desiredMode;
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
// The staff panel has no slot in the original markup, so it is wrapped in the
// same shell the other panels use rather than styled separately.
// "N of M done" as words, not a bar. The count is the honest summary of a task
// list and stays readable at a glance on a phone.
// The file can carry more than one project - the plane's own milestones and the
// product it is building - so entries are grouped in the order the file lists
// them. Ungrouped entries render as one flat list, exactly as before.
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

/* ===========================================================================
   WATCHTOWER LAYOUT
   Panels in priority order: does this need me -> is the machinery healthy ->
   who is working -> what is queued or blocked -> what just happened -> how are
   the projects progressing.

   Every state carries a WORD. Where a coloured dot or bar appears it sits
   beside its label, never instead of one: the reader is colour-weak, so colour
   is reinforcement and never the message.

   Only fields present in /api/v1/state are read - nothing is invented. Where
   the engine does not report something the mock-up asked for, the panel says
   what it actually has rather than filling the shape with a plausible number.
   =========================================================================== */

/* Inline SVG rather than an icon font: the page must render with the machine
   offline, and a missing glyph would be invisible rather than obviously wrong.
   Every icon is decorative - the word beside it carries the meaning. */
const WT_ICONS = {
  shield: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>',
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  hourglass: '<path d="M6 2h12"/><path d="M6 22h12"/><path d="M6 2c0 5 6 5 6 10 0-5 6-5 6-10"/><path d="M6 22c0-5 6-5 6-10 0 5 6 5 6 10"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/>',
  github: '<path d="M9 19c-4 1.4-4-2.2-6-2.6m12 5.6v-3.6a3.1 3.1 0 0 0-.9-2.4c2.9-.3 6-1.4 6-6.4a4.9 4.9 0 0 0-1.4-3.4 4.6 4.6 0 0 0-.1-3.4S17.4 2 15 3.6a12.4 12.4 0 0 0-6.4 0C6.2 2 5.2 2.8 5.2 2.8a4.6 4.6 0 0 0-.1 3.4A4.9 4.9 0 0 0 3.7 9.6c0 5 3 6.1 5.9 6.4a3.1 3.1 0 0 0-.9 2.4V22"/>',
  calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 11h18"/>',
  radar: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/><path d="M12 12 19 7"/>',
  external: '<path d="M14 4h6v6"/><path d="M20 4 10 14"/><path d="M18 13v6a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h6"/>',
  chevron: '<path d="m9 5 7 7-7 7"/>',
  chevronDown: '<path d="m5 9 7 7 7-7"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1"/>',
  copy: '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
  merge: '<circle cx="7" cy="18" r="3"/><circle cx="7" cy="6" r="3"/><circle cx="18" cy="12" r="3"/><path d="M7 9v6"/><path d="M10 6h3a2 2 0 0 1 2 2v2"/>'
};

// Hoisted, not const: the module's initial loadDashboard() call sits near the
// top of the file and renders before evaluation ever reaches this block.
function icon(name, size = 15) {
  const body = WT_ICONS[name];
  if (!body) return "";
  return `<svg class="wt-ico" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none"
    stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"
    aria-hidden="true">${body}</svg>`;
}

const WT_HEALTHY = new Set(["ok", "healthy", "green"]);

/* Owner-qualified repository names are long and the owner is always the same
   person, so the prefix carries no information on this page. */
function shortRepo(value) {
  const name = String(value || "");
  return name.includes("/") ? name.slice(name.lastIndexOf("/") + 1) : name;
}

function sevGlyph(sev) {
  return sev === "down" ? "&#10007;" : sev === "attention" ? "!" : "&#10003;";
}

function panelHead(title, iconName, right) {
  return `<div class="wt-head">
    <div class="wt-h">${icon(iconName, 16)}${escapeHtml(title)}</div>
    ${right || ""}
  </div>`;
}

/* A dot alone would be unreadable to this reader, so it never ships without
   the word next to it. */
function dotLabel(sev, label) {
  const cls = sev === "down" ? "is-bad" : sev === "attention" ? "is-attn" : sev === "ok" ? "is-ok" : "";
  return `<span class="wt-svc-item"><span class="wt-dot ${cls}" aria-hidden="true"></span>${escapeHtml(label)}</span>`;
}

function chevronLink(url, label) {
  if (!url) return "";
  return `<a class="wt-chev" href="${escapeAttribute(url)}" target="_blank" rel="noreferrer"
    aria-label="${escapeAttribute(label || "Open")}">${icon("chevron", 17)}</a>`;
}

function actionButton(url, label) {
  if (!url) return "";
  return `<a class="wt-btn" href="${escapeAttribute(url)}" target="_blank" rel="noreferrer">${icon("github", 15)}${escapeHtml(label)}</a>`;
}

/* The owner reads this page; owner-facing surfaces are in their own timezone.
   Durable records elsewhere stay UTC - this is a reading convenience only, and
   it is labelled so nobody mistakes it for the stored value. */
function localStamp(value) {
  if (!value) return "unknown";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "unknown";
  return d.toLocaleString("en-US", {
    timeZone: "America/New_York",
    day: "numeric", month: "short", year: "numeric",
    hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false
  }) + " ET";
}

function clockOnly(value) {
  if (!value) return "";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleTimeString("en-US", {
    timeZone: "America/New_York", hour: "2-digit", minute: "2-digit", hour12: false
  });
}

/* ---------- header ---------- */
function renderHeaderRight() {
  const host = document.getElementById("header-right");
  if (!host) return;
  host.innerHTML = `
    <label class="wt-switch">
      <input type="checkbox" id="auto-refresh">
      <span class="wt-track" aria-hidden="true"></span>
      <span>Auto-refresh</span>
    </label>
    <button type="button" class="wt-btn" data-action="refresh">${icon("refresh", 15)}Refresh now</button>
    <div class="wt-stamp">
      <b>Last updated</b>
      <span>${escapeHtml(localStamp(state.lastLoadedAt))}</span>
    </div>`;
}

/* LIVE is a claim about this tab, not about the server: it is driven by how
   old the rendered view is, so a frozen background tab reads STALE instead of
   showing an hour-old number as if it were current. */
function renderLiveBadge() {
  const badge = document.getElementById("live-badge");
  if (!badge) return;
  const age = viewAgeMs();
  const stale = age == null || age > staleAfterMs;
  badge.classList.toggle("is-stale", stale);
  badge.textContent = stale ? "STALE" : "LIVE";
}

/* ---------- 1. does this need me ---------- */
function renderAttentionPanel() {
  const host = document.getElementById("panel-attention");
  if (!host) return;
  /* No snapshot is not the same as nothing to report. Saying "nothing is
     waiting on you" while blind is the exact failure this page exists to
     avoid, so an unread state says so and reads as a fault. */
  const blind = !state.snapshot;
  const attention = state.snapshot?.attention;
  const items = attention?.items || [];
  const level = String(attention?.level || "").toLowerCase();
  const sev = blind || level === "down" || level === "critical"
    ? "down"
    : items.length ? "attention" : "ok";

  host.classList.toggle("is-attn", sev === "attention");
  host.classList.toggle("is-bad", sev === "down");
  host.classList.toggle("is-ok", sev === "ok");

  const tone = sev === "down" ? "var(--wt-bad)" : sev === "attention" ? "var(--wt-attn)" : "var(--wt-ok)";

  /* Yours first, and everything else marked with whose it is. The panel is
     titled "needs your attention"; an item nobody can act on from here, listed
     without saying so, is what taught the reader to bring all of it to a person. */
  const mine = items.filter(i => (i.actor || "owner") === "owner");
  const theirs = items.filter(i => (i.actor || "owner") !== "owner");

  const row = i => {
    const s = String(i.severity || "").toLowerCase() === "down" ? "down" : "attention";
    const actor = i.actor || "owner";
    const word = actor === "owner" ? "YOURS" : actor === "plane" ? "PLANE" : "OPERATOR";
    /* One line per item. The "why" is a hover tooltip here rather than a
       paragraph: this panel exists to be counted at a glance, and the same
       detail is printed in full in the queue panel below, so nothing is lost. */
    return `
      <div class="wt-item is-tight ${actor === "owner" ? "sev-" + s : ""}" title="${escapeAttribute(i.detail || "")}">
        <span class="wt-badge ${actor === "owner" ? "sev-" + s : ""}">${word}</span>
        <div class="wt-item-body">
          <div class="wt-item-title wt-clip">${escapeHtml(i.label || "")}</div>
        </div>
        <div class="wt-item-right">
          ${renderAttentionAction(i.action, i)}
          ${chevronLink(i.url, i.label)}
        </div>
      </div>`;
  };

  const rows = mine.map(row).join("") + (theirs.length
    ? `<div class="wt-subhead">Being handled &mdash; shown so you can see why</div>${theirs.map(row).join("")}`
    : "");

  host.innerHTML = `
    <div class="wt-attn-top">
      <span class="wt-attn-glyph" style="color:${tone}" aria-hidden="true">
        ${sev === "ok"
          ? icon("shield", 28)
          : `<span style="font-size:30px;font-weight:700;line-height:1">${sevGlyph(sev)}</span>`}
      </span>
      <div style="min-width:0">
        <div class="wt-attn-eyebrow">Needs your attention</div>
        <h1 class="wt-attn-headline">${escapeHtml(blind
          ? "Cannot read the plane"
          : attention?.headline || "Nothing is waiting on you")}</h1>
        <p class="wt-attn-sub">${escapeHtml(blind
          ? (state.error?.message || state.error || "No snapshot has loaded, so nothing on this page is current. This says nothing about whether work is waiting.")
          : attention?.detail || "The plane is running normally.")}</p>
      </div>
    </div>
    ${rows ? `<div class="wt-items">${rows}</div>` : ""}`;
}

/* An item the reader cannot act on does not belong on a panel addressed to them.
   Where the action needs a terminal the exact command is offered, copyable, not
   described: "its last run exited with code -2147023829" is not an action;
   `schtasks /run /tn "ADCP Commander"` is. */
function renderAttentionAction(action, item) {
  if (!action) {
    return item?.url ? actionLink(item.url, "Open") : "";
  }

  /* A command is shown, not offered as a button.
     It ran as a <button> that only copied text to the clipboard, which is a
     control that does not do the thing it is labelled with - and one of the
     commands behind it did not exist at all. A thing that looks pressable has to
     act; anything else is text, and this is text. */
  if (action.kind === "command" && action.command) {
    return `<code class="wt-cmd" title="Run this yourself">${escapeHtml(action.command)}</code>`;
  }

  /* A directive is the one thing the page can actually DO. It posts through the
     plane, which holds the tracker key; the browser has no credentials of its
     own. Everything else here is a link, and says so by being one. */
  if (action.kind === "directive" && action.issue_id) {
    return `<button type="button" class="wt-btn" data-action="post-directive"
      data-issue-id="${escapeAttribute(action.issue_id)}"
      data-issue-identifier="${escapeAttribute(action.issue_identifier || "")}"
      data-repository="${escapeAttribute(action.repository || "")}"
      data-directive-action="${escapeAttribute(action.directive_action || "resume")}"
      data-directive-phase="${escapeAttribute(action.directive_phase || "")}"
      >${icon("refresh", 14)}${escapeHtml(action.label)}</button>`;
  }

  const url = action.url || item?.url;
  if (!url) return "";
  return `<a class="wt-btn" href="${escapeAttribute(url)}" target="_blank" rel="noreferrer">${
    icon(action.kind === "merge" ? "merge" : "github", 14)}${escapeHtml(action.label)}</a>`;
}

/* ---------- 2. is the machinery healthy ---------- */
function renderHealthPanel() {
  const host = document.getElementById("panel-health");
  if (!host) return;
  const snap = state.snapshot || {};
  const reach = snap.tracker_reachability || {};
  const tasks = snap.watched_tasks || [];

  /* Positive evidence only. Before the first load every field is empty, and
     "no failures reported" would otherwise render as a clean bill of health. */
  const blind = !state.snapshot;
  const trackerOk = !blind && !reach.unreachable_since;
  const engineOk = state.health?.ok === true;
  /* The engine reports "ok" here, not "healthy" - matching only "healthy"
     painted two on-schedule tasks as late and the whole panel as degraded. */
  const lateTasks = tasks.filter(t => !WT_HEALTHY.has(String(t.health || "").toLowerCase()));
  const allOk = !blind && trackerOk && engineOk && lateTasks.length === 0;

  const sched = tasks.map(t => {
    const status = String(t.status || t.state || "").toLowerCase();
    const running = status.includes("running");
    const healthy = WT_HEALTHY.has(String(t.health || "").toLowerCase());
    const word = running ? "Running" : healthy ? "Ready" : (t.health || "Unknown");
    const cls = running ? "is-run" : healthy ? "is-ok" : "is-bad";
    return `
      <div class="wt-sched-row">
        <span class="wt-sched-ico" aria-hidden="true">${icon(running ? "radar" : "calendar", 16)}</span>
        <div class="wt-sched-main">
          <div class="wt-sched-name">${escapeHtml(t.name || "Scheduled task")}</div>
          <div class="wt-sched-why">${escapeHtml(t.explanation || "")}</div>
        </div>
        <div class="wt-sched-right">
          <div class="wt-sched-state ${cls}">${escapeHtml(word)}</div>
          <div class="wt-sched-every">${t.expect_every_minutes ? `Every ${escapeHtml(String(t.expect_every_minutes))}m` : "&mdash;"}</div>
        </div>
      </div>`;
  }).join("");

  const age = viewAgeMs();
  const ageLabel = age == null ? "never" : `${formatDurationFromMilliseconds(age)} ago`;

  host.innerHTML = `
    ${panelHead("Health & freshness", "shield", `
      <div class="wt-headmeta">
        <span class="wt-svc-item">Tracker <span class="wt-dot ${blind ? "" : trackerOk ? "is-ok" : "is-bad"}" aria-hidden="true"></span>
          <b>${blind ? "Unknown" : trackerOk ? "Reachable" : "Unreachable"}</b></span>
        <span>Consecutive failures <b>${blind ? "&mdash;" : escapeHtml(String(reach.consecutive_failures ?? 0))}</b></span>
      </div>`)}

    <div class="wt-health-line">
      <div class="wt-health-state" style="color:${allOk ? "var(--wt-ok)" : blind ? "var(--wt-bad)" : "var(--wt-attn)"}">
        <span aria-hidden="true">${sevGlyph(allOk ? "clear" : blind ? "down" : "attention")}</span>
        ${allOk ? "All systems operational" : blind ? "Cannot reach the engine" : "Something needs looking at"}
      </div>
      <span class="wt-pill ${allOk ? "sev-ok" : "sev-attention"}">Updated ${escapeHtml(ageLabel)}</span>
    </div>
    <div class="wt-health-sub">
      Engine: <b>${escapeHtml(state.health?.label || "Unknown")}</b>
      &middot; Last tracker success: ${escapeHtml(localStamp(reach.last_success))}
    </div>

    ${tasks.length
      ? `<div class="wt-sub-h">Scheduler <span>(Windows Task Scheduler)</span></div>
         <div class="wt-sched">${sched}</div>`
      : `<div class="wt-empty">No scheduled tasks are being watched, so a dead scheduler would not show here.</div>`}`;
}

/* ---------- 3. who is working ---------- */
function avatarFor(name, role) {
  const n = String(name || "");
  const cls = role === "owner" ? "is-owner"
    : /claude/i.test(n) ? "is-claude"
      : /codex/i.test(n) ? "is-codex" : "";
  /* Two initials, not one: "Claude" and "Codex" both start with C, and this
     reader cannot separate them by the chip tint. */
  const word = n.trim().split(/[^A-Za-z0-9]+/)[0] || "";
  const letter = role === "owner" ? "" : (word.slice(0, 2) || "?").toUpperCase();
  return `<span class="wt-avatar ${cls}" aria-hidden="true">${letter || icon("user", 14)}</span>`;
}

function renderTeamPanel() {
  const host = document.getElementById("panel-team");
  if (!host) return;
  const staff = state.snapshot?.staff || [];

  const rows = staff.map(m => {
    const word = { working: "WORKING", idle: "IDLE", waiting: "WAITING", late: "LATE" }[m.state]
      || String(m.state || "").toUpperCase();
    /* Idle is the normal, healthy resting state and is deliberately NOT styled
       as a problem - a page that flags calm teaches its reader to ignore it. */
    const sev = m.state === "working" ? "ok" : m.state === "late" ? "down" : m.state === "waiting" ? "attention" : "";
    const elapsed = m.elapsed_seconds != null ? formatDurationFromMilliseconds(m.elapsed_seconds * 1000) : "&mdash;";
    return `
      <tr>
        <td>
          <div class="wt-agent">
            ${avatarFor(m.runner, m.role)}
            <div class="wt-who"><b>${escapeHtml(m.runner || "")}</b><span>${escapeHtml(m.role || "")}</span></div>
          </div>
        </td>
        <td><span class="wt-badge ${sev ? "sev-" + sev : ""}">${word}</span></td>
        <td class="wt-act">${escapeHtml(m.activity || "")}</td>
        <td class="wt-num">${elapsed}</td>
      </tr>`;
  }).join("");

  host.innerHTML = `
    ${panelHead("What the team is doing", "users", `<span class="wt-count">${staff.length} agents</span>`)}
    ${staff.length
      ? `<table class="wt-table">
           <thead><tr><th>Agent / Role</th><th>State</th><th>Current activity</th><th style="text-align:right">Elapsed</th></tr></thead>
           <tbody>${rows}</tbody>
         </table>
         <div class="wt-foot-link"><button type="button" class="wt-link" data-action="toggle-section" data-section="advanced">View all agents and activity ${icon("chevron", 13)}</button></div>`
      : `<div class="wt-empty">No workers configured.</div>`}`;
}

/* ---------- 5. what just happened ---------- */
function renderActivityPanel() {
  const host = document.getElementById("panel-activity");
  if (!host) return;
  const open = state.expanded.activity;

  const agent = (state.snapshot?.agent_activity || []).map(a => ({
    at: a.at, who: a.actor || "agent", text: a.summary || "", url: a.url || null, kind: "agent"
  }));
  const events = (state.snapshot?.activity || []).map(e => ({
    at: e.at, who: "engine", text: e.message || e.label || e.event || "", url: null, kind: "event"
  }));
  const all = [...agent, ...events].filter(r => r.text).sort((a, b) => new Date(b.at) - new Date(a.at));

  /* Both an absolute clock and a relative age: the clock lets the reader line
     an event up against what they were doing, the age says how fresh it is. */
  const rows = (open ? all.slice(0, 24) : all.slice(0, 5)).map(r => `
      <div class="wt-frow">
        ${avatarFor(r.kind === "agent" ? r.who : "Engine", "")}
        <div class="wt-fbody"><div class="wt-fsum">${escapeHtml(r.text)}</div></div>
        <div class="wt-ftime">
          <b>${escapeHtml(clockOnly(r.at))}</b>
          <span>${escapeHtml(formatRelativeTime(r.at))}</span>
        </div>
        ${r.url
          ? `<a class="wt-fopen" href="${escapeAttribute(r.url)}" target="_blank" rel="noreferrer"
               aria-label="Open">${icon("external", 15)}</a>`
          : `<span class="wt-fopen" style="opacity:0" aria-hidden="true">${icon("external", 15)}</span>`}
      </div>`).join("");

  host.innerHTML = `
    ${panelHead("Recent activity", "clock", `<button type="button" class="wt-link" data-action="toggle-section" data-section="activity">${open ? "Show less" : "View all"}</button>`)}
    ${rows ? `<div class="wt-feed">${rows}</div>` : `<div class="wt-empty">Nothing reported yet.</div>`}`;
}

/* ---------- 4. what is queued or blocked, and why ---------- */
function renderQueuePanel() {
  const host = document.getElementById("panel-queue");
  if (!host) return;
  const open = state.expanded.queue;

  /* Four situations needing four different responses, which one flat "queue"
     would merge into a single undifferentiated list. Every category is derived
     from fields that already exist - waiting_on, checks_state and the attention
     severities - not from a new backend contract. */
  const rows = [];

  for (const pr of state.snapshot?.open_pull_requests || []) {
    const checks = String(pr.checks_state || "").toUpperCase();
    if (checks === "FAILURE" || checks === "ERROR") {
      rows.push({
        word: "BLOCKED", sev: "down",
        title: `PR #${pr.number}`,
        why: `${pr.title || ""} - failing CI checks, so the merge gate will not take it.`,
        pill: pr.updated_at ? `Waiting ${formatRelativeTime(pr.updated_at).replace(/ ago$/, "")}` : "",
        pillIcon: "clock", pillSev: "down",
        url: pr.url
      });
    }
  }

  for (const item of state.snapshot?.attention?.items || []) {
    const label = item.label || "";
    /* A PR that fell out of the pipeline is a fault to repair, not a decision
       to make - the engine's own detail text says so. Filing it under "your
       decision" told the reader to choose something when nothing is on offer. */
    const orphaned = /fell out of the pipeline|is not tracking it/i.test(`${label} ${item.detail || ""}`);
    if (!orphaned && !/needs a decision|stopped at the merge gate|waiting on you/i.test(label)) continue;
    rows.push({
      word: orphaned ? "NEEDS REPAIR" : "AWAITING DECISION",
      sev: orphaned ? "down" : "attention",
      title: label, why: item.detail || "",
      pill: orphaned ? "To repair" : "Your decision",
      pillIcon: orphaned ? "refresh" : "user",
      pillSev: orphaned ? "down" : "attention",
      url: item.url
    });
  }

  /* Retrying work is queued work: it is not running and it has a reason. The
     countdown is clamped at zero so an overdue retry never reads as a future one. */
  for (const retry of state.snapshot?.retrying || []) {
    rows.push({
      word: "RETRYING", sev: "attention",
      title: retry.issue_identifier || `#${retry.issue_id ?? ""}`,
      why: retry.reason || "The last attempt failed; the plane will try again.",
      pill: `Next ${formatRetryCountdown(retry.due_at)}`, pillIcon: "clock", pillSev: "attention",
      url: retry.url
    });
  }

  for (const q of state.snapshot?.queue || []) {
    const w = String(q.waiting_on || "");
    const word = /pipeline/i.test(w) ? "IN PIPELINE" : /free slot/i.test(w) ? "WAITING SLOT" : "NEXT UP";
    rows.push({
      word, sev: "",
      title: `${q.issue_identifier || ""}${q.repository ? ` &middot; ${escapeHtml(shortRepo(q.repository))}` : ""}`,
      titleRaw: true,
      why: q.title || w,
      pill: "", url: q.url
    });
  }

  const shown = open ? rows : rows.slice(0, 4);
  const html = shown.map(r => `
      <div class="wt-item sev-${r.sev || "clear"}">
        <span class="wt-badge is-block ${r.sev ? "sev-" + r.sev : ""}">${r.word}</span>
        <div class="wt-item-body">
          <div class="wt-item-title">${r.titleRaw ? r.title : escapeHtml(r.title)}</div>
          <div class="wt-item-why">${escapeHtml(r.why)}</div>
        </div>
        <div class="wt-item-right">
          ${r.pill ? `<span class="wt-pill sev-${r.pillSev || ""}">${icon(r.pillIcon || "clock", 14)}${escapeHtml(r.pill)}</span>` : ""}
          ${chevronLink(r.url, r.title)}
        </div>
      </div>`).join("");

  host.innerHTML = `
    ${panelHead("Queue / blocked work", "hourglass", `<span class="wt-count">${rows.length}</span>`)}
    ${html
      ? `<div class="wt-items">${html}</div>
         ${rows.length > shown.length || open
           ? `<div class="wt-foot-link"><button type="button" class="wt-link" data-action="toggle-section" data-section="queue">${
               open ? "Show less" : `View all queue and blocked items`} ${icon("chevron", 13)}</button></div>`
           : ""}`
      : `<div class="wt-empty">Nothing queued or blocked. Label an issue <span class="wt-mono">symphony-ready</span> to give the plane work.</div>`}`;
}

/* ---------- 6. how are the projects progressing ---------- */
function renderRoadmapPanel() {
  const host = document.getElementById("panel-roadmap");
  if (!host) return;
  const items = state.snapshot?.roadmap || [];
  const open = state.expanded.roadmap;

  const groups = [];
  for (const it of items) {
    const name = it.group || "";
    let g = groups.find(x => x.name === name);
    if (!g) { g = { name, items: [] }; groups.push(g); }
    g.items.push(it);
  }

  /* The per-project tallies come from work already on the page, so a project
     line and the queue above it can never disagree. */
  const attentionItems = state.snapshot?.attention?.items || [];
  const failingPrs = (state.snapshot?.open_pull_requests || [])
    .filter(p => ["FAILURE", "ERROR"].includes(String(p.checks_state || "").toUpperCase()));

  const body = groups.map(g => {
    const expandedProject = !!state.expandedProjects[g.name || ""];
    const activeAt = g.items.findIndex(i => i.status === "active");
    /* Collapsed shows where the work IS: the stage before, the active one, and
       the next two. The full history is a weekly question, not a per-glance one. */
    const slice = open
      ? g.items
      : activeAt >= 0
        ? g.items.slice(Math.max(0, activeAt - 1), activeAt + 3)
        : g.items.filter(i => i.status !== "done").slice(0, 3);

    const statusWord = e => e.status === "done" ? "Complete" : e.status === "active" ? "In progress" : "Planned";
    const stepClass = e => e.status === "done" ? "is-done" : e.status === "active" ? "is-active" : "";
    const stepMark = e => e.status === "done" ? "&#10003;" : String(g.items.indexOf(e) + 1);

    const steps = slice.map((entry, idx) => {
      /* Cut on a word boundary: a hard slice left labels ending in a dangling dash. */
      const raw = entry.milestone || entry.title || "";
      const label = raw.length <= 22 ? raw : `${raw.slice(0, 22).replace(/[\s—-]+\S*$/, "")}…`;
      /* The milestone is a name, not a description - "Stage 2" says nothing about
         what stage 2 is. The description lives in title, so it goes on the hover
         and, for the stage actually being worked, in the caption below the rail. */
      return `${idx ? `<span class="wt-step-bar" aria-hidden="true"></span>` : ""}
        <div class="wt-step ${stepClass(entry)}" title="${escapeAttribute(entry.title || "")}">
          <span class="wt-step-dot" aria-hidden="true">${stepMark(entry)}</span>
          <div class="wt-step-txt"><b>${escapeHtml(label)}</b><span>${statusWord(entry)}</span></div>
        </div>`;
    }).join("");

    /* Every stage with its description, so "what is stage 2 for" is answerable
       without leaving the panel or hovering anything. */
    const detail = expandedProject
      ? `<div class="wt-stage-list">${g.items.map(entry => `
          <div class="wt-stage ${stepClass(entry)}">
            <span class="wt-step-dot" aria-hidden="true">${stepMark(entry)}</span>
            <div class="wt-stage-txt">
              <b>${escapeHtml(entry.milestone || "")}</b>
              <span class="wt-stage-status">${statusWord(entry)}</span>
              <div class="wt-stage-why">${escapeHtml(entry.title || "")}</div>
            </div>
          </div>`).join("")}</div>`
      : "";

    /* Attribute an item to a project only on a distinctive word from its name.
       Splitting on the first token matched "the" against half the page. */
    const keys = String(g.name || "").toLowerCase().split(/[^a-z0-9]+/).filter(w => w.length >= 5);
    const mine = t => keys.length > 0 && keys.some(k => String(t).toLowerCase().includes(k));
    const decisions = attentionItems.filter(i => mine(`${i.label} ${i.url}`)).length;
    const reds = failingPrs.filter(p => mine(`${p.title} ${p.url}`)).length;

    const tallies = [
      decisions ? dotLabel("attention", `${decisions} ${decisions === 1 ? "issue" : "issues"} awaiting decision`) : "",
      reds ? dotLabel("down", `${reds} ${reds === 1 ? "PR" : "PRs"} failing checks`) : ""
    ].filter(Boolean).join("");

    const activeEntry = activeAt >= 0 ? g.items[activeAt] : null;

    return `
      <div class="wt-proj">
        <div class="wt-proj-head">
          <button type="button" class="wt-disc" data-action="toggle-project"
                  data-project="${escapeAttribute(g.name || "")}"
                  aria-expanded="${expandedProject ? "true" : "false"}"
                  title="${expandedProject ? "Hide every stage" : "Show every stage and what it is for"}">
            <span class="wt-disc-ico ${expandedProject ? "is-open" : ""}" aria-hidden="true">${icon("chevron", 15)}</span>
            <span class="wt-proj-name">${escapeHtml(g.name || "Roadmap")}</span>
          </button>
          ${activeAt >= 0 ? `<span class="wt-proj-tag">Current focus</span>` : ""}
          <span class="wt-count">${g.items.filter(i => i.status === "done").length}/${g.items.length} complete</span>
        </div>
        <div class="wt-steps">${steps}</div>
        ${activeEntry?.title
          ? `<div class="wt-proj-sum"><b>${escapeHtml(activeEntry.milestone || "Now")}:</b> ${escapeHtml(activeEntry.title)}</div>`
          : ""}
        ${detail}
        <div class="wt-proj-foot">
          ${tallies ? `<span>Active items</span><span class="wt-tally">${tallies}</span>` : `<span>No open items on this project.</span>`}
        </div>
      </div>`;
  }).join("");

  host.innerHTML = `
    ${panelHead("Projects / roadmap", "folder", `<button type="button" class="wt-link" data-action="toggle-section" data-section="roadmap">${open ? "Show less" : "View full roadmap"} ${icon("chevron", 13)}</button>`)}
    ${body || `<div class="wt-empty">No roadmap configured.</div>`}`;
}

/* ---------- footer strip ---------- */
function renderUtilityStrip() {
  const host = document.getElementById("panel-utility");
  if (!host) return;
  const snap = state.snapshot || {};
  const reach = snap.tracker_reachability || {};
  const tasks = snap.watched_tasks || [];
  const lateTasks = tasks.filter(t => !WT_HEALTHY.has(String(t.health || "").toLowerCase()));

  const blind = !state.snapshot;
  const services = [
    blind
      ? dotLabel("down", "GitHub API unknown")
      : dotLabel(reach.unreachable_since ? "down" : "ok", `GitHub API ${reach.unreachable_since ? "unreachable" : "reachable"}`),
    blind
      ? dotLabel("down", "Windows Task Scheduler unknown")
      : dotLabel(lateTasks.length ? "attention" : "ok",
          `Windows Task Scheduler ${lateTasks.length ? `${lateTasks.length} late` : `${tasks.length} on schedule`}`),
    dotLabel(state.health?.ok === true ? "ok" : "down", `Engine ${state.health?.label || "unknown"}`)
  ].join("");

  /* The mock-up put a GitHub API request budget here. The engine does not
     report one - rate_limits is the agent vendors' quota - so this shows the
     quota it actually has rather than a plausible number for a different thing. */
  const byRunner = snap.rate_limits_by_runner || {};
  const meters = Object.entries(byRunner).map(([name, limits]) => {
    const pct = Math.round(Number(limits?.primary?.usedPercent ?? limits?.usedPercent ?? 0));
    const cls = pct >= 90 ? "is-full" : pct >= 70 ? "is-high" : "";
    const word = pct >= 90 ? "critical" : pct >= 70 ? "high" : "ok";
    return `<span class="wt-svc-item">${escapeHtml(name)}
      <span class="wt-bar ${cls}" role="img" aria-label="${pct}% used, ${word}"><i style="width:${Math.min(100, pct)}%"></i></span>
      <span class="wt-lim-num">${pct}% used</span></span>`;
  }).join("");

  host.innerHTML = `
    <div class="wt-strip"><span class="wt-strip-h">Services</span>${services}</div>
    <div class="wt-strip wt-lim">
      <span class="wt-strip-h">Agent quota</span>
      ${meters || `<span class="wt-empty">No quota reported.</span>`}
    </div>`;
}

/* ---------- advanced ---------- */
function renderAdvancedPanel() {
  const host = document.getElementById("panel-advanced");
  if (!host) return;
  const open = state.expanded.advanced;

  host.innerHTML = `
    ${panelHead("Advanced", "hourglass", `<button type="button" class="wt-link" data-action="toggle-section" data-section="advanced">${open ? "Hide" : "Show workflow editor"} ${icon("chevron", 13)}</button>`)}
    ${open ? `<div class="wt-adv-body"><div id="workflow-editor"></div></div>` : ""}`;
}
