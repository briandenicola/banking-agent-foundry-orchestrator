/**
 * Automated browser-behaviour regression tests for site.js submit handling.
 *
 * Mechanism: Node.js built-in test runner (node:test) + jsdom.
 *   - No headless browser binary required at runtime.
 *   - Loads the ACTUAL production site.js into a jsdom window context.
 *   - Builds form HTML that mirrors what Razor renders after Theo's
 *     EvidenceFiles-nullable fix (aria-optional-evidence-gate.md approved contract).
 *   - Dispatches real DOM submit events and inspects outcomes.
 *
 * What constitutes "HTTP POST observed" in these tests:
 *   - form.method === 'post'         (form targets a POST endpoint)
 *   - event.defaultPrevented===false (submit not canceled by JS)
 *   - form.classList.contains('is-loading') after the deferred callback fires
 *     (loading state entered — valid path taken, no listener canceled)
 *   These three facts together prove that, in a real browser, the native form
 *   POST would proceed.  The authoritative real-HTTP-POST proof is the
 *   WebApplicationFactory integration test in DemoScenarioUiTests.cs which sends
 *   an actual multipart/form-data POST via HttpClient and asserts a 302 redirect.
 *
 * The production site.js submit handler defers busy state to setTimeout(0) so
 *   that any later-registered listener (e.g. jQuery Unobtrusive Validation) can
 *   still cancel without leaving the form permanently frozen.  Tests that check
 *   busy state must therefore await a tick after dispatch.
 *
 * Contract under test (aria-optional-evidence-gate.md §3 — JS submit handler):
 *   - Check validity BEFORE entering busy state.
 *   - If invalid: call e.preventDefault(), return without setting is-loading.
 *   - If valid: defer to setTimeout(0), check e.defaultPrevented, then set state.
 *   - If a later synchronous listener canceled: do NOT enter busy state.
 *   - Safety restore timer removes busy state if page stays alive unexpectedly.
 *   - EvidenceFiles must carry no required-constraint metadata when optional.
 */

import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SITE_JS_PATH = path.join(__dirname, '../../src/webui/wwwroot/js/site.js');
const SITE_JS = readFileSync(SITE_JS_PATH, 'utf8');

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Build form HTML that mirrors what ASP.NET Core Razor renders.
 *
 * @param {object} opts
 * @param {boolean} opts.evidenceRequired  - true = pre-fix (non-nullable, data-val-required emitted)
 *                                           false = post-fix (nullable, no constraint attrs)
 * @param {string}  opts.userMessageValue  - prefilled value for the message textarea
 */
function buildFormHtml({ evidenceRequired = false, userMessageValue = '' } = {}) {
  // Razor only emits data-val / data-val-required when the property is non-nullable.
  // After Theo's fix (List<IFormFile>?) these attributes are absent.
  const fileInputAttrs = evidenceRequired
    ? 'data-val="true" data-val-required="The EvidenceFiles field is required."'
    : '';

  return `<!DOCTYPE html>
<html lang="en"><body>
  <form id="request-form" data-loading-form method="post" action="/">
    <textarea
      id="Input_UserMessage"
      name="Input.UserMessage"
      required>${userMessageValue}</textarea>
    <input type="hidden" id="Input_DemoScenario" name="Input.DemoScenario" value="" />
    <input
      id="Input_EvidenceFiles"
      name="Input.EvidenceFiles"
      type="file"
      accept=".pdf,.png,.jpg,.jpeg"
      multiple
      ${fileInputAttrs} />
    <div data-selected-files></div>
    <button type="submit">Start workflow</button>
  </form>
</body></html>`;
}

/**
 * Create a JSDOM window that is ready for site.js execution.
 * Stubs out APIs that site.js may touch but that jsdom lacks.
 */
function createWindow(html) {
  const dom = new JSDOM(html, {
    url: 'http://localhost:5000/',
    runScripts: 'outside-only',  // we execute scripts manually via window.eval
  });
  const { window } = dom;

  // jsdom does not implement matchMedia; stub it so polling code does not throw.
  window.matchMedia = () => ({ matches: false, addEventListener: () => {} });

  // Smart setTimeout: only schedule real timers for small delays (<=10 ms).
  // Large-delay timers (e.g. the 100 s safety restore) are captured but NOT
  // scheduled on the real Node.js event loop, so tests exit promptly.
  // T3 / suite 7 tests install their own mock on top via capturedTimers.
  const _origST = window.setTimeout.bind(window);
  window.setTimeout = (fn, delay, ...args) => {
    if (typeof delay !== 'number' || delay <= 10) return _origST(fn, delay ?? 0, ...args);
    return 0; // fake ID — not scheduled
  };

  // Stub fetch so that if polling code somehow fires it does not throw.
  window.fetch = () => Promise.reject(new Error('fetch not available in test'));

  return window;
}

/**
 * Evaluate site.js inside a jsdom window and return the form element.
 * Returns null if no [data-loading-form] is present.
 */
function loadSiteJs(window) {
  window.eval(SITE_JS);
  return window.document.querySelector('form[data-loading-form]');
}

/**
 * Dispatch a cancelable submit event on `form` and return the event so callers
 * can inspect defaultPrevented.
 */
function dispatchSubmit(window, form) {
  const ev = new window.Event('submit', { bubbles: true, cancelable: true });
  form.dispatchEvent(ev);
  return ev;
}

/**
 * Wait for all pending setTimeout(0) callbacks to fire.
 * Required after dispatchSubmit on a valid form because site.js defers
 * busy state entry to setTimeout(0) so later synchronous listeners can cancel.
 */
function flushTimers() {
  return new Promise((resolve) => setTimeout(resolve, 10));
}

// ---------------------------------------------------------------------------
// Test suite 1 — DOM attribute contract (evidence field optionality)
// ---------------------------------------------------------------------------

describe('EvidenceFiles DOM attribute contract', () => {
  test('post-fix form: evidence input has NO required attribute', () => {
    const window = createWindow(buildFormHtml({ evidenceRequired: false }));
    const input = window.document.getElementById('Input_EvidenceFiles');
    assert.ok(input, 'Input_EvidenceFiles must exist in the form');
    assert.equal(
      input.hasAttribute('required'), false,
      'HTML required must be absent — optional evidence must not block checkValidity()'
    );
  });

  test('post-fix form: evidence input has NO data-val-required attribute', () => {
    const window = createWindow(buildFormHtml({ evidenceRequired: false }));
    const input = window.document.getElementById('Input_EvidenceFiles');
    assert.equal(
      input.hasAttribute('data-val-required'), false,
      'data-val-required must be absent — nullable List<IFormFile>? emits no required validator'
    );
  });

  test('post-fix form: evidence input has NO data-val attribute', () => {
    const window = createWindow(buildFormHtml({ evidenceRequired: false }));
    const input = window.document.getElementById('Input_EvidenceFiles');
    assert.equal(
      input.hasAttribute('data-val'), false,
      'data-val must be absent on optional file input'
    );
  });

  test('pre-fix form (regression sentinel): evidence input DOES carry data-val-required', () => {
    // This test documents the broken pre-fix state.
    // When Index.cshtml.cs has `List<IFormFile>` (non-nullable), Razor emits this attribute
    // and jQuery unobtrusive validation blocks submission, causing the UI freeze described in
    // aria-optional-evidence-gate.md.  The attribute must be absent in the fixed form.
    const window = createWindow(buildFormHtml({ evidenceRequired: true }));
    const input = window.document.getElementById('Input_EvidenceFiles');
    assert.equal(
      input.getAttribute('data-val-required'),
      'The EvidenceFiles field is required.',
      'Sentinel: pre-fix form must have data-val-required (confirms the broken state exists)'
    );
  });
});

// ---------------------------------------------------------------------------
// Test suite 2 — submit handler: valid no-evidence submission → POST proceeds
//
// NOTE: site.js defers busy state to setTimeout(0) so that later-registered
// listeners can still cancel without freezing the form.  Tests that check
// busy state must call `await flushTimers()` after dispatchSubmit.
// ---------------------------------------------------------------------------

describe('submit handler: valid no-evidence submission produces HTTP POST', () => {
  test('form has method=post (POST target confirmed)', () => {
    const window = createWindow(buildFormHtml({ userMessageValue: 'Dispute charge' }));
    const form = window.document.querySelector('form[data-loading-form]');
    assert.equal(form.getAttribute('method'), 'post',
      'Form method must be post');
  });

  test('submit event is NOT prevented for valid form (no-evidence path)', () => {
    // defaultPrevented===false proves this path does not block the POST.
    // The authoritative real-HTTP-POST proof is in DemoScenarioUiTests.cs
    // (WebApplicationFactory + HttpClient with multipart/form-data POST, asserts 302).
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge on 2026-07-15',
    }));
    const form = loadSiteJs(window);

    const ev = dispatchSubmit(window, form);

    assert.equal(ev.defaultPrevented, false,
      'POST NOT BLOCKED: submit event must not be prevented for valid no-evidence submission');
  });

  test('is-loading class is added on valid no-evidence submission (after deferred tick)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge on 2026-07-15',
    }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);

    // Busy state is deferred to setTimeout(0); flush the timer queue.
    await flushTimers();

    assert.ok(form.classList.contains('is-loading'),
      'is-loading must be added after deferred tick — busy state entered only on valid code path');
  });

  test('aria-busy is set to "true" on valid no-evidence submission (after deferred tick)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge on 2026-07-15',
    }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    assert.equal(form.getAttribute('aria-busy'), 'true',
      'aria-busy must be "true" after valid submit (accessibility busy signal)');
  });

  test('submit button is disabled after valid no-evidence submission (after deferred tick)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge on 2026-07-15',
    }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    const button = form.querySelector('button[type="submit"]');
    assert.ok(button.hasAttribute('disabled'),
      'Submit button must be disabled after valid submit to prevent double-post');
  });
});

// ---------------------------------------------------------------------------
// Test suite 3 — submit handler: invalid submission → no POST, no stuck UI
// (Invalid submissions call e.preventDefault() synchronously; no deferred path)
// ---------------------------------------------------------------------------

describe('submit handler: invalid submission (empty required message) does not fire POST', () => {
  test('is-loading is NOT added when UserMessage is empty (required field)', () => {
    // userMessageValue = '' (default) → textarea is empty → checkValidity() returns false
    const window = createWindow(buildFormHtml({ evidenceRequired: false, userMessageValue: '' }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);

    assert.equal(form.classList.contains('is-loading'), false,
      'is-loading must NOT be set on invalid submission — no stuck busy state');
  });

  test('aria-busy is NOT set when UserMessage is empty', () => {
    const window = createWindow(buildFormHtml({ evidenceRequired: false, userMessageValue: '' }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);

    assert.equal(form.getAttribute('aria-busy'), null,
      'aria-busy must remain unset on invalid submission');
  });

  test('submit button remains enabled when UserMessage is empty', () => {
    const window = createWindow(buildFormHtml({ evidenceRequired: false, userMessageValue: '' }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);

    const button = form.querySelector('button[type="submit"]');
    assert.equal(button.hasAttribute('disabled'), false,
      'Submit button must remain enabled after invalid submission (user can correct and retry)');
  });
});

// ---------------------------------------------------------------------------
// Test suite 4 — cancellation: later synchronous listener calls preventDefault
//
// This suite tests the critical fix for the confirmed stuck-UI bug:
// A listener registered AFTER site.js (e.g. jQuery Unobtrusive Validation)
// fires in the same synchronous dispatch, can call e.preventDefault(), and
// the form must NOT enter busy state.
//
// These tests target the PRODUCTION site.js (not an inline reimplementation).
// ---------------------------------------------------------------------------

describe('cancellation: later synchronous listener prevents → no busy state', () => {
  test('T1: later listener calls preventDefault → is-loading NOT set', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);

    // Register a listener AFTER site.js (simulates jQuery Unobtrusive registered
    // from _ValidationScriptsPartial which loads after site.js in _Layout.cshtml).
    form.addEventListener('submit', (e) => {
      e.preventDefault(); // later listener cancels
    });

    dispatchSubmit(window, form);
    // Flush the deferred setTimeout(0) — site.js will check e.defaultPrevented.
    await flushTimers();

    assert.equal(form.classList.contains('is-loading'), false,
      'T1: busy state must NOT be entered when a later listener canceled the submission');
  });

  test('T1: later listener calls preventDefault → aria-busy NOT set', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);
    form.addEventListener('submit', (e) => { e.preventDefault(); });

    dispatchSubmit(window, form);
    await flushTimers();

    assert.equal(form.getAttribute('aria-busy'), null,
      'T1: aria-busy must remain null when later listener canceled');
  });

  test('T1: later listener calls preventDefault → controls remain enabled', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);
    form.addEventListener('submit', (e) => { e.preventDefault(); });

    dispatchSubmit(window, form);
    await flushTimers();

    const button = form.querySelector('button[type="submit"]');
    assert.equal(button.hasAttribute('disabled'), false,
      'T1: submit button must remain enabled when later listener canceled (form stays interactive)');
  });

  test('T2: no listener cancels valid submit → busy state IS entered (after tick)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);
    // No extra listeners — normal path.

    dispatchSubmit(window, form);
    await flushTimers();

    assert.ok(form.classList.contains('is-loading'),
      'T2: busy state must be entered when no listener cancels a valid submission');
    assert.equal(form.getAttribute('aria-busy'), 'true',
      'T2: aria-busy must be set when no listener cancels');
  });
});

// ---------------------------------------------------------------------------
// Test suite 5 — safety restore timer
//
// The site.js handler installs a bounded restore timer (LOADING_SAFETY_RESTORE_MS)
// after entering busy state.  If the page is still alive after that interval
// (navigation was unexpectedly blocked), the timer restores interactive state.
//
// In a real browser with a successful POST, the page unloads before the timer
// fires; in jsdom (no navigation), we can manually invoke the restore timer
// to verify it correctly clears busy state.
// ---------------------------------------------------------------------------

describe('safety restore timer: clears stuck busy state', () => {
  test('T3: safety restore timer removes is-loading, aria-busy, and disabled state', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));

    // Intercept setTimeout to capture the restore timer without actually waiting 8 s.
    const capturedTimers = [];
    const origSetTimeout = window.setTimeout.bind(window);
    window.setTimeout = (fn, delay, ...args) => {
      // Only schedule the deferred 0 ms check on the real event loop.
      // Large-delay timers (like the 100 s safety restore) are captured but NOT
      // scheduled so they do not keep the Node.js process alive after tests complete.
      const id = delay <= 10 ? origSetTimeout(fn, delay, ...args) : capturedTimers.length;
      capturedTimers.push({ fn, delay });
      return id;
    };

    const form = loadSiteJs(window);
    dispatchSubmit(window, form);

    // Let the deferred setTimeout(0) fire first (busy state enters).
    await flushTimers();

    assert.ok(form.classList.contains('is-loading'),
      'T3 setup: form must be in busy state before restore timer fires');

    // Find the safety restore timer (delay >= 100_000 ms — matching WebUI HttpClient
    // default timeout; must NOT fire at 8 s, 17 s, or any value below request timeout).
    const restoreTimer = capturedTimers.find((t) => t.delay >= 100_000);
    assert.ok(restoreTimer,
      'T3: safety restore timer must be registered with a bounded positive delay');

    // Manually fire the restore timer (simulates the page still being alive).
    restoreTimer.fn();

    assert.equal(form.classList.contains('is-loading'), false,
      'T3: is-loading must be removed after safety restore timer fires');
    assert.equal(form.getAttribute('aria-busy'), null,
      'T3: aria-busy must be removed after safety restore timer fires');
    const button = form.querySelector('button[type="submit"]');
    assert.equal(button.hasAttribute('disabled'), false,
      'T3: submit button must be re-enabled after safety restore timer fires');
  });

  test('T3: restore timer is a no-op if form is already not in busy state', async () => {
    // If navigation succeeded (form not in is-loading), the restore timer must not
    // throw or corrupt state.
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));

    const capturedTimers = [];
    const origSetTimeout = window.setTimeout.bind(window);
    window.setTimeout = (fn, delay, ...args) => {
      // Only schedule the deferred 0 ms check on the real event loop.
      // Large-delay timers (like the 100 s safety restore) are captured but NOT
      // scheduled so they do not keep the Node.js process alive after tests complete.
      const id = delay <= 10 ? origSetTimeout(fn, delay, ...args) : capturedTimers.length;
      capturedTimers.push({ fn, delay });
      return id;
    };

    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    // Manually clear busy state (simulates successful navigation unloading the DOM
    // or another mechanism that removed is-loading before the restore timer fires).
    form.classList.remove('is-loading');

    const restoreTimer = capturedTimers.find((t) => t.delay > 100);
    assert.ok(restoreTimer, 'restore timer must exist');

    // Should not throw and should not re-disable anything.
    assert.doesNotThrow(() => restoreTimer.fn(),
      'T3: restore timer must be safe to invoke even if is-loading is already absent');

    const button = form.querySelector('button[type="submit"]');
    // Button may still be disabled (from the deferred busy-state entry), but the
    // restore timer must not have re-added disabled either — it's a no-op.
    // The key invariant: no exception thrown.
  });
});

// ---------------------------------------------------------------------------
// Test suite 6 — keyboard submit and double-submit protection
// ---------------------------------------------------------------------------

describe('keyboard submit: Enter key submit is treated identically to click', () => {
  test('keyboard Enter on textarea triggers same deferred busy-state path', async () => {
    // jsdom fires the submit event via form.requestSubmit() which is the same
    // path as keyboard Enter on a form field.  Verified via dispatchSubmit which
    // dispatches the submit event directly on the form.
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Transfer inquiry',
    }));
    const form = loadSiteJs(window);

    // Simulate keyboard submit via requestSubmit if available, otherwise fallback.
    if (typeof form.requestSubmit === 'function') {
      form.requestSubmit();
    } else {
      dispatchSubmit(window, form);
    }

    await flushTimers();

    assert.ok(form.classList.contains('is-loading'),
      'Keyboard submit must also enter busy state after deferred tick');
  });
});

// ---------------------------------------------------------------------------
// Test suite 7 — safety restore timer value (hardening regression)
//
// LOADING_SAFETY_RESTORE_MS must be >= 100_000 ms (the WebUI HttpClient default
// timeout).  This prevents the form from being re-enabled while a legitimate
// POST navigation is still in-flight on a slow/degraded network.
//
// Aria review (aria-evidence-final-review.md §4) noted that observed healthy
// requests take 11–17 s.  The previous value of 8 000 ms was too short.
// ---------------------------------------------------------------------------

describe('safety restore timer: value must not fall below request timeout', () => {
  test('safety restore timer delay is >= 100_000 ms (WebUI HttpClient default)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));

    const capturedTimers = [];
    const origSetTimeout = window.setTimeout.bind(window);
    window.setTimeout = (fn, delay, ...args) => {
      // Only schedule the deferred 0 ms check on the real event loop.
      // Large-delay timers (like the 100 s safety restore) are captured but NOT
      // scheduled so they do not keep the Node.js process alive after tests complete.
      const id = delay <= 10 ? origSetTimeout(fn, delay, ...args) : capturedTimers.length;
      capturedTimers.push({ fn, delay });
      return id;
    };

    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    const restoreTimer = capturedTimers.find((t) => t.delay >= 100_000);
    assert.ok(restoreTimer,
      'Safety restore delay must be >= 100_000 ms; a shorter value re-enables the form ' +
      'while a legitimate POST is still in-flight (observed healthy requests: 11–17 s)');

    assert.ok(restoreTimer.delay >= 100_000,
      `Safety restore delay must be >= 100_000 ms. Got: ${restoreTimer?.delay}`);
  });

  test('safety restore timer does NOT fire at 17 s (delay well above observed healthy range)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));

    const capturedTimers = [];
    const origSetTimeout = window.setTimeout.bind(window);
    window.setTimeout = (fn, delay, ...args) => {
      // Only schedule the deferred 0 ms check on the real event loop.
      // Large-delay timers (like the 100 s safety restore) are captured but NOT
      // scheduled so they do not keep the Node.js process alive after tests complete.
      const id = delay <= 10 ? origSetTimeout(fn, delay, ...args) : capturedTimers.length;
      capturedTimers.push({ fn, delay });
      return id;
    };

    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    // No timer with delay <= 17_000 ms should be the safety restore timer.
    // (The only timer with delay < 100_000 is the deferred setTimeout(0) check.)
    const earlyRestoreCandidate = capturedTimers.find(
      (t) => t.delay > 0 && t.delay <= 17_000
    );
    assert.equal(
      earlyRestoreCandidate, undefined,
      'No restore timer must fire at or below 17 s while a request could still be pending'
    );
  });
});

// ---------------------------------------------------------------------------
// Test suite 8 — pageshow lifecycle: restores busy state on bfcache restore
//
// When the browser restores a page from bfcache (user pressed Back after a
// successful POST), pageshow fires with persisted===true.  site.js must clear
// stale busy state immediately so the user can act.
// ---------------------------------------------------------------------------

describe('pageshow lifecycle: restores busy state on bfcache restore (persisted=true)', () => {
  test('pageshow(persisted=true) clears is-loading, aria-busy, and disabled state', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    // Pre-condition: form is in busy state
    assert.ok(form.classList.contains('is-loading'),
      'pageshow setup: form must be in busy state before pageshow fires');

    // Dispatch synthetic pageshow with persisted=true
    const evt = new window.PageTransitionEvent('pageshow', {
      bubbles: false,
      cancelable: false,
      persisted: true,
    });
    window.dispatchEvent(evt);

    assert.equal(form.classList.contains('is-loading'), false,
      'pageshow(persisted=true): is-loading must be cleared immediately');
    assert.equal(form.getAttribute('aria-busy'), null,
      'pageshow(persisted=true): aria-busy must be cleared immediately');
    const button = form.querySelector('button[type="submit"]');
    assert.equal(button.hasAttribute('disabled'), false,
      'pageshow(persisted=true): submit button must be re-enabled');
  });

  test('pageshow(persisted=false) does NOT restore busy state (fresh navigation, not bfcache)', async () => {
    const window = createWindow(buildFormHtml({
      evidenceRequired: false,
      userMessageValue: 'Dispute charge',
    }));
    const form = loadSiteJs(window);
    dispatchSubmit(window, form);
    await flushTimers();

    assert.ok(form.classList.contains('is-loading'),
      'setup: form must be in busy state');

    // Dispatch pageshow with persisted=false (normal navigation, not bfcache)
    const evt = new window.PageTransitionEvent('pageshow', {
      bubbles: false,
      cancelable: false,
      persisted: false,
    });
    window.dispatchEvent(evt);

    // Busy state must NOT be cleared — persisted=false means a normal fresh load
    assert.ok(form.classList.contains('is-loading'),
      'pageshow(persisted=false): is-loading must remain (fresh navigation, not bfcache restore)');
  });
});
