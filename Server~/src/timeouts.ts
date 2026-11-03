/**
 * Bridge wait times. These are fixed here on purpose - no tool exposes them as a parameter.
 *
 * They used to be caller-settable with a generous schema ceiling, and that is exactly how a
 * five-minute wait got through: the defaults were short, but an explicit argument walked straight
 * past them. The file bridge does not cancel a request when we stop waiting - the editor keeps
 * processing it - so a long wait buys nothing over a short one plus a re-query. Nobody outside
 * this file gets to lengthen them.
 */

/** Nothing here may exceed this. Guards the constants below and any wait computed at runtime. */
export const MAX_TIMEOUT_MS = 60_000;

/** One bridge command waiting for its response file. */
export const BRIDGE_TIMEOUT_MS = 2_000;

/** Waiting out a compile, polled across the domain reload it triggers. */
export const COMPILE_WAIT_MS = 30_000;

/** Polling one editor field until it reaches a value. */
export const FIELD_WAIT_MS = 6_000;

/** Waiting for a modal dialog to appear before giving up on it. */
export const DIALOG_ARM_MS = 1_500;

/** Batch-mode test run. Not a bridge call - it boots a second Unity, which alone outlasts the cap. */
export const TEST_RUN_TIMEOUT_MS = 3 * 60 * 1_000;

/** Pulls any wait - constant above, or a remainder computed mid-flow - under the ceiling. */
export function clampTimeout(ms: number): number {
  return Math.min(ms, MAX_TIMEOUT_MS);
}
