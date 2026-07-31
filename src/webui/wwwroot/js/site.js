/* =========================================================
 * Banking Agent — Workspace JS
 * Handles: demo scenarios, file display, loading states,
 *          async workflow polling, stage progress, a11y.
 * ========================================================= */

// ── Demo scenario selection ──────────────────────────────
const requestInput = document.querySelector("#Input_UserMessage");
const demoScenarioInput = document.querySelector("#Input_DemoScenario");
const scenarioButtons = document.querySelectorAll("[data-scenario]");

scenarioButtons.forEach((button) => {
  button.addEventListener("click", () => {
    if (!requestInput || !demoScenarioInput) return;
    requestInput.value = button.dataset.prompt ?? "";
    demoScenarioInput.value = button.dataset.scenario ?? "";
    scenarioButtons.forEach((candidate) => {
      candidate.classList.toggle("is-selected", candidate === button);
      candidate.setAttribute("aria-pressed", candidate === button ? "true" : "false");
    });
    requestInput.focus();
  });
});

requestInput?.addEventListener("input", () => {
  if (!demoScenarioInput) return;
  demoScenarioInput.value = "";
  scenarioButtons.forEach((button) => {
    button.classList.remove("is-selected");
    button.setAttribute("aria-pressed", "false");
  });
});

// ── Form loading state (submit + approval forms) ─────────
//
// The busy-state is entered AFTER all synchronous submit listeners have run.
// This ensures any listener registered after site.js (e.g. jQuery Unobtrusive
// Validation, registered from _ValidationScriptsPartial which loads after
// site.js in _Layout.cshtml) can still cancel the submission without leaving
// the form permanently frozen.
//
// Mechanism:
//  1. Run validation (jQuery .valid() or native checkValidity()).
//     - Invalid: call e.preventDefault() and return immediately. No busy state.
//     - Valid: capture the event, then defer to setTimeout(0).
//  2. In the deferred callback, re-check e.defaultPrevented.
//     - If a later synchronous listener called preventDefault(), skip busy state.
//     - Otherwise: enter busy state (is-loading + disabled controls + aria-busy).
//  3. Set a bounded safety restore timer. For native POST navigation the page
//     unloads and this timer fires harmlessly. If navigation was unexpectedly
//     blocked after busy state was entered, the timer restores interactive state.
//
// Double-submit protection: controls are disabled after the deferred check
// confirms the POST will proceed.

// How long to wait before safety-restoring a stuck busy state (last-resort fallback).
//
// A native POST navigation unloads the page before any timer fires; for those
// paths the timer is harmless.  The value must exceed the longest realistic
// pending-navigation duration so that a legitimate but slow server response is
// never interrupted.  The WebUI orchestrator client uses the HttpClient default
// timeout of 100 s, so the safety fallback is set to match.
//
// For bfcache-restored pages (user pressed Back after a successful navigation)
// the `pageshow` listener below clears busy state immediately, independently of
// this timer.
//
// Do NOT lower this value below the request timeout — doing so would
// re-enable the form while a valid POST is still in-flight, allowing
// double-submission on slow networks.
const LOADING_SAFETY_RESTORE_MS = 100_000;

document.querySelectorAll("[data-loading-form]").forEach((form) => {
  form.addEventListener("submit", (e) => {
    // Run jQuery unobtrusive validation first if present; fall back to native.
    const jqForm = window.jQuery?.(form);
    const isValid = jqForm?.valid?.() ?? form.checkValidity();
    if (!isValid) {
      e.preventDefault();
      return; // leave controls and labels untouched
    }

    // Defer busy state until after all synchronous dispatch listeners have run.
    // Closing over `e` lets the deferred callback inspect e.defaultPrevented.
    setTimeout(() => {
      if (e.defaultPrevented) return; // a later listener canceled — stay interactive

      form.classList.add("is-loading");
      // Disable all interactive elements to prevent double-submit.
      form.querySelectorAll("button, input, select, textarea").forEach((el) => {
        el.setAttribute("disabled", "");
        el.setAttribute("aria-disabled", "true");
      });
      form.setAttribute("aria-busy", "true");

      // Safety restore: if the page is still alive after the bounded wait,
      // something unexpectedly blocked navigation — restore interactive state.
      setTimeout(() => {
        if (!form.classList.contains("is-loading")) return;
        form.classList.remove("is-loading");
        form.querySelectorAll("button, input, select, textarea").forEach((el) => {
          el.removeAttribute("disabled");
          el.removeAttribute("aria-disabled");
        });
        form.removeAttribute("aria-busy");
      }, LOADING_SAFETY_RESTORE_MS);
    }, 0);
  });
});


// ── Navigation lifecycle: restore on bfcache page restore ────────────────
//
// When the browser restores a page from the back-forward cache (user pressed
// Back after a successful POST navigation), `pageshow` fires with persisted===true.
// The new-page response has already been received; the old page's busy state is
// stale.  Restore all loading-form controls immediately so the user can act.
window.addEventListener("pageshow", (e) => {
  if (!e.persisted) return;
  document.querySelectorAll("[data-loading-form].is-loading").forEach((form) => {
    form.classList.remove("is-loading");
    form.querySelectorAll("button, input, select, textarea").forEach((el) => {
      el.removeAttribute("disabled");
      el.removeAttribute("aria-disabled");
    });
    form.removeAttribute("aria-busy");
  });
});
// ── Evidence file display ────────────────────────────────
const evidenceInput = document.querySelector("#Input_EvidenceFiles");
const selectedFiles = document.querySelector("[data-selected-files]");
if (evidenceInput && selectedFiles) {
  evidenceInput.addEventListener("change", () => {
    selectedFiles.replaceChildren();
    Array.from(evidenceInput.files ?? []).forEach((file) => {
      const item = document.createElement("span");
      item.className = "selected-file-item";
      item.textContent = `${file.name} · ${(file.size / 1024 / 1024).toFixed(1)} MB`;
      selectedFiles.appendChild(item);
    });
  });
}

// ── Workflow polling ──────────────────────────────────────
//
// Bounded exponential back-off poll of GET ?handler=Poll&workflowId={id}.
// Stops when status is terminal (Completed | Failed | Rejected)
// or WaitingForApproval (user must act).
// Shows a timeout notice at 90 s with Refresh / New request actions.
//
// Evidence ordering: POST /workflows is submitted with ExpectsEvidence=true
// when files are present; the API defers the immediate trigger until
// after evidence upload, so we do not need extra coordination here.

const workflowPanel = document.querySelector("[data-workflow-id]");
if (workflowPanel) {
  const workflowId = workflowPanel.dataset.workflowId;
  const isPolling = workflowPanel.dataset.polling === "true";
  const TERMINAL_STATUSES = new Set(["Completed", "Failed", "Rejected", "WaitingForApproval"]);

  if (isPolling && workflowId) {
    startPolling(workflowId);
  }
}

function startPolling(workflowId) {
  const liveRegion = document.getElementById("live-status-region");
  const statusPill = document.getElementById("status-pill");
  const timeoutNotice = document.getElementById("timeout-notice");
  const TERMINAL = new Set(["Completed", "Failed", "Rejected", "WaitingForApproval"]);

  // Exponential back-off: 1s → 2s → 4s → 8s → cap at 10s
  const INTERVALS = [1000, 2000, 4000, 8000, 10000];
  const MAX_DURATION_MS = 90_000;

  let attempt = 0;
  let elapsed = 0;
  let timeoutId = null;
  let cancelled = false;

  // Prefer reduced-motion: flatten intervals if OS preference is set
  const prefersReducedMotion =
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const pollInterval = (idx) =>
    prefersReducedMotion
      ? 5000
      : INTERVALS[Math.min(idx, INTERVALS.length - 1)];

  function stopPolling() {
    cancelled = true;
    if (timeoutId !== null) clearTimeout(timeoutId);
  }

  async function poll() {
    if (cancelled) return;

    try {
      const res = await fetch(
        `?handler=Poll&workflowId=${encodeURIComponent(workflowId)}`,
        {
          method: "GET",
          headers: { Accept: "application/json" },
          signal: AbortSignal.timeout(15_000),
        }
      );

      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: "Unknown error" }));
        announceStatus(liveRegion, `Status unavailable: ${err.error ?? res.statusText}`);
        scheduleNext();
        return;
      }

      const data = await res.json();
      if (!data.status) {
        scheduleNext();
        return;
      }

      // Update status pill
      if (statusPill) {
        statusPill.textContent = data.status;
        statusPill.className = `status-pill ${statusClass(data.status)}`;
      }

      // Update the stage track
      updateStages(data);

      // Update technical details
      updateSummary(data);

      // Update timeline
      updateTimeline(data.events ?? []);

      // Update support case
      updateSupportCase(data.supportCase);

      // Announce status change to screen readers
      announceStatus(liveRegion, statusAnnouncement(data));

      if (TERMINAL.has(data.status)) {
        stopPolling();
        // Hide processing indicator
        const spinner = document.querySelector(".processing-indicator");
        if (spinner) spinner.hidden = true;
        // For WaitingForApproval: show the approval form (page reload needed for the form)
        if (data.status === "WaitingForApproval") {
          window.location.reload();
        }
        // For Failed: show the timeout/failure notice area
        if (data.status === "Failed") {
          window.location.reload();
        }
        return;
      }

      scheduleNext();
    } catch (err) {
      if (err.name === "TimeoutError" || err.name === "AbortError") {
        announceStatus(liveRegion, "Status request timed out — retrying…");
      }
      scheduleNext();
    }
  }

  function scheduleNext() {
    const delay = pollInterval(attempt);
    elapsed += delay;
    attempt++;

    if (elapsed >= MAX_DURATION_MS) {
      stopPolling();
      const spinner = document.querySelector(".processing-indicator");
      if (spinner) spinner.hidden = true;
      if (timeoutNotice) timeoutNotice.hidden = false;
      announceStatus(liveRegion, "Processing is taking longer than expected. Use the Refresh button to check status.");
      return;
    }

    timeoutId = setTimeout(poll, delay);
  }

  // Start first poll after a short delay so the page finishes rendering
  timeoutId = setTimeout(poll, 800);
}

// ── UI update helpers ─────────────────────────────────────

function statusClass(status) {
  switch (status) {
    case "Completed": return "status-completed";
    case "WaitingForApproval": return "status-waiting";
    case "Rejected": return "status-rejected";
    case "Failed": return "status-failed";
    default: return "status-active";
  }
}

function statusAnnouncement(data) {
  switch (data.status) {
    case "Draft": return "Workflow accepted. Starting processing.";
    case "Recovering": return "Workflow is being processed by the agent network.";
    case "WaitingForApproval": return "Workflow is waiting for your approval. The approval form is now shown.";
    case "Completed": return "Workflow completed successfully.";
    case "Failed": return "Workflow processing failed. See the timeline for details.";
    case "Rejected": return "Decision recorded. Workflow rejected.";
    default: return `Status: ${data.status}`;
  }
}

function announceStatus(region, message) {
  if (!region) return;
  // Replace content to trigger aria-live re-announcement
  region.textContent = "";
  // Slight delay ensures aria-live fires consistently across ATs
  requestAnimationFrame(() => {
    region.textContent = message;
  });
}

function updateStages(data) {
  const stageTrack = document.querySelector(".stage-track");
  if (!stageTrack) return;
  const stages = stageTrack.querySelectorAll(".stage");

  // Derive stage from audit event types returned by the server (GET /api/v1/workflows/{id}).
  // This is truthful — no version proxy, no invented success.
  //   Plan done  : "workflow.plan" event present
  //   Decide done: terminal event present ("workflow.completed" | "workflow.approval_required" | "workflow.failed")
  const events = data.events ?? [];
  const hasEvent = (type) => events.some((e) => e.type === type);
  const planDone = hasEvent("workflow.plan");
  const terminalDone = hasEvent("workflow.completed") ||
    hasEvent("workflow.approval_required") ||
    hasEvent("workflow.failed");
  const status = data.status;

  stages.forEach((el) => {
    el.classList.remove("stage-active", "stage-done");
  });

  if (!planDone) {
    // Planning in progress (Draft / Recovering or failed before planner completed)
    stages[0]?.classList.add("stage-active");
  } else if (!terminalDone) {
    // Planner done; specialist/routing in progress
    stages[0]?.classList.add("stage-done");
    stages[1]?.classList.add("stage-active");
  } else {
    // Specialist done; move to Decide
    stages[0]?.classList.add("stage-done");
    stages[1]?.classList.add("stage-done");
    if (status === "WaitingForApproval") {
      stages[2]?.classList.add("stage-active");
    } else {
      stages[2]?.classList.add("stage-done");
    }
  }
}

function updateSummary(data) {
  const intentEl = document.getElementById("workflow-intent");
  if (intentEl) intentEl.textContent = data.intent ?? "Being determined";

  const updatedEl = document.getElementById("workflow-updated");
  if (updatedEl && data.updatedAt) {
    updatedEl.textContent = new Date(data.updatedAt).toLocaleString("en-US", {
      month: "short", day: "numeric", hour: "numeric", minute: "2-digit"
    });
  }

  const techStatus = document.getElementById("tech-status");
  if (techStatus) techStatus.textContent = data.status;

  const techVersion = document.getElementById("tech-version");
  if (techVersion) techVersion.textContent = data.version;
}

function updateTimeline(events) {
  const list = document.getElementById("timeline-list");
  const countEl = document.getElementById("event-count");
  if (!list) return;

  // Sort newest-first
  const sorted = [...events].sort((a, b) =>
    new Date(b.timestamp) - new Date(a.timestamp));

  list.replaceChildren(
    ...sorted.map((ev) => {
      const li = document.createElement("li");
      li.innerHTML = `
        <span class="timeline-dot"></span>
        <div>
          <div class="timeline-meta">
            <strong>${escHtml(ev.message)}</strong>
            <time datetime="${escHtml(ev.timestamp)}">${formatTime(ev.timestamp)}</time>
          </div>
          <small>${escHtml(ev.type.replace("workflow.", "").replace(/_/g, " "))} · ${escHtml(ev.actor ?? "system")}</small>
          ${ev.details ? `<p>${escHtml(ev.details)}</p>` : ""}
        </div>`;
      return li;
    })
  );

  if (countEl) countEl.textContent = `${events.length} event${events.length !== 1 ? "s" : ""}`;
}

function updateSupportCase(supportCase) {
  if (!supportCase) return;
  // Support case only appears after approval — trigger a reload to show full card
  const existing = document.querySelector(".support-card");
  if (!existing) {
    window.location.reload();
  }
}

function formatTime(isoString) {
  if (!isoString) return "";
  return new Date(isoString).toLocaleTimeString("en-US", {
    hour: "numeric", minute: "2-digit", second: "2-digit"
  });
}

function escHtml(str) {
  if (!str) return "";
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
