// Japanese wording for the two quantities every screen shows.
//
// The upstream helpers speak English — "1.2 MB", "2m 3s" — because the pages
// they were written for do. A Japanese screen that says 約 1s next to
// かかる目安 has the units in one language and the sentence in another, which
// reads as a half-translated tool. These are the same numbers in the same
// coarseness, said in Japanese.

/** Byte counts. Coarse on purpose: this lands next to a file name in a status
 *  line, not in a report. */
export function bytesJa(bytes: number): string {
  if (bytes < 1024) return `${bytes} バイト`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/**
 * Durations, rounded the way somebody waiting would round them.
 *
 * Never "0 秒": a transfer that has not finished takes at least a moment, and
 * a countdown that reads zero while the bar is still moving reads as stuck.
 */
export function durationJa(seconds: number): string {
  const total = Math.max(1, Math.ceil(seconds));
  if (total < 60) return `${total} 秒`;
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  if (minutes < 60) return rest === 0 ? `${minutes} 分` : `${minutes} 分 ${rest} 秒`;
  const hours = Math.floor(minutes / 60);
  const restMinutes = minutes % 60;
  return restMinutes === 0 ? `${hours} 時間` : `${hours} 時間 ${restMinutes} 分`;
}
