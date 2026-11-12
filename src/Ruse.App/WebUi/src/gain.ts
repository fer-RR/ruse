/** Formats a linear gain (1.0 = 0 dB) for compact volume readouts. */
export function formatGainDb(value: number): string {
  if (!Number.isFinite(value) || value <= 0.0001) {
    return '\u2212\u221E dB';
  }

  const db = 20 * Math.log10(value);
  const rounded = Math.abs(db) < 0.05 ? 0 : db;
  return `${rounded > 0 ? '+' : ''}${rounded.toFixed(1)} dB`;
}

/**
 * Keyboard step for linear gain sliders. Arrow keys use 1% (or 0.1% with
 * Shift); Home returns to unity and End goes to the maximum.
 */
export function nextVolumeValue(
  event: { key: string; shiftKey: boolean },
  value: number,
  max = 1.5,
): number | null {
  const step = event.shiftKey ? 0.001 : 0.01;
  let next: number;
  switch (event.key) {
    case 'ArrowLeft':
    case 'ArrowDown':
      next = value - step;
      break;
    case 'ArrowRight':
    case 'ArrowUp':
      next = value + step;
      break;
    case 'Home':
      next = 1;
      break;
    case 'End':
      next = max;
      break;
    default:
      return null;
  }

  return Math.max(0, Math.min(max, Number(next.toFixed(3))));
}

