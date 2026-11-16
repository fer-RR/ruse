type Rgba = [number, number, number, number];

export interface TimelinePalette {
  background: string;
  ruler: string;
  rulerLine: string;
  grid: string;
  gridMajor: string;
  beatMajor: string;
  beatMinor: string;
  rulerText: string;
  trackA: string;
  trackB: string;
  clip: string;
  generatorClip: string;
  clipStroke: string;
  generatorStroke: string;
  generatorHatch: string;
  waveform: string;
  fade: string;
  clipText: string;
  xfadeFill: string;
  xfadeStroke: string;
  dragPreview: string;
  dragLabel: string;
  automationBackground: string;
  automationLine: string;
  automationText: string;
  automationCurve: string;
  automationPoint: string;
  automationPointActive: string;
  labelBackground: string;
  labelText: string;
  loopFill: string;
  loopStroke: string;
  marker: string;
  markerText: string;
  regionFill: string;
  regionStroke: string;
  playhead: string;
}

const parseColor = (value: string): Rgba => {
  const text = value.trim();
  const hex = text.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (hex) {
    const digits = hex[1].length === 3
      ? hex[1].split('').map(part => part + part).join('')
      : hex[1];
    return [
      parseInt(digits.slice(0, 2), 16),
      parseInt(digits.slice(2, 4), 16),
      parseInt(digits.slice(4, 6), 16),
      1,
    ];
  }

  const rgb = text.match(/rgba?\(([^)]+)\)/i);
  if (rgb) {
    const parts = rgb[1].split(',').map(part => Number(part.trim()));
    return [
      parts[0] || 0,
      parts[1] || 0,
      parts[2] || 0,
      Number.isFinite(parts[3]) ? parts[3] : 1,
    ];
  }

  return [0, 0, 0, 1];
};

const mix = (from: Rgba, to: Rgba, amount: number): Rgba => {
  const t = Math.max(0, Math.min(1, amount));
  return [
    Math.round(from[0] + (to[0] - from[0]) * t),
    Math.round(from[1] + (to[1] - from[1]) * t),
    Math.round(from[2] + (to[2] - from[2]) * t),
    from[3] + (to[3] - from[3]) * t,
  ];
};

const rgba = (color: Rgba, alpha = color[3]) =>
  `rgba(${color[0]}, ${color[1]}, ${color[2]}, ${Math.max(0, Math.min(1, alpha))})`;

const black: Rgba = [0, 0, 0, 1];
const white: Rgba = [255, 255, 255, 1];

export function readTimelinePalette(): TimelinePalette {
  const styles = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string) =>
    parseColor(styles.getPropertyValue(name) || fallback);

  const canvas = read('--canvas', '#171e29');
  const surface0 = read('--surface-0', '#1d2531');
  const text2 = read('--text-2', '#a3b0c2');
  const text3 = read('--text-3', '#74839a');
  const cyan = read('--cyan', '#65d6d0');
  const blue = read('--blue', '#5b8def');
  const amber = read('--amber', '#e5ad51');
  const signal = read('--signal', '#43e39a');
  const record = read('--record', '#ff8b72');

  const background = mix(canvas, black, 0.58);
  const trackBase = mix(background, surface0, 0.14);
  const accent = mix(background, cyan, 0.58);
  const generator = mix(background, blue, 0.58);

  return {
    background: rgba(background),
    ruler: rgba(mix(background, black, 0.16)),
    rulerLine: rgba(mix(background, white, 0.16)),
    grid: rgba(white, 0.06),
    gridMajor: rgba(white, 0.13),
    beatMajor: rgba(white, 0.09),
    beatMinor: rgba(white, 0.04),
    rulerText: rgba(mix(text3, white, 0.2), 0.78),
    trackA: rgba(trackBase),
    trackB: rgba(mix(background, surface0, 0.22)),
    clip: rgba(accent),
    generatorClip: rgba(generator),
    clipStroke: rgba(mix(cyan, white, 0.35)),
    generatorStroke: rgba(mix(blue, white, 0.35)),
    generatorHatch: rgba(mix(blue, white, 0.5), 0.2),
    waveform: rgba(mix(cyan, white, 0.55)),
    fade: rgba(amber),
    clipText: rgba(mix(text2, white, 0.35)),
    xfadeFill: rgba(amber, 0.14),
    xfadeStroke: rgba(amber),
    dragPreview: rgba(accent),
    dragLabel: rgba(mix(amber, white, 0.35)),
    automationBackground: rgba(mix(background, black, 0.28)),
    automationLine: rgba(mix(background, white, 0.18)),
    automationText: rgba(mix(text3, white, 0.18)),
    automationCurve: rgba(mix(signal, white, 0.18)),
    automationPoint: rgba(mix(signal, white, 0.3)),
    automationPointActive: rgba(amber),
    labelBackground: rgba(mix(background, black, 0.22), 0.9),
    labelText: rgba(mix(text2, white, 0.16)),
    loopFill: rgba(cyan, 0.1),
    loopStroke: rgba(mix(cyan, white, 0.12)),
    marker: rgba(amber, 0.92),
    markerText: rgba(mix(amber, white, 0.35)),
    regionFill: rgba(blue, 0.08),
    regionStroke: rgba(blue, 0.45),
    playhead: rgba(record),
  };
}
