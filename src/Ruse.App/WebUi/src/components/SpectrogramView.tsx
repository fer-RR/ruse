import { useEffect, useRef } from 'react';
import { useI18n } from '../i18n';

const BANDS = 256;
const WIDTH = 428;
const HEIGHT = 240;
const DATA_HEIGHT = HEIGHT - 12;
const FREQUENCY_REFS = [
  [20000, '20k'], [10000, '10k'], [5000, '5k'], [2000, '2k'],
  [1000, '1k'], [500, '500'], [250, '250'], [100, '100'],
] as const;

function formatHz(value: number | undefined): string {
  if (!value || !Number.isFinite(value)) return '';
  return value >= 1000 ? `${(value / 1000).toFixed(1)} kHz` : `${Math.round(value)} Hz`;
}

function color(value: number): [number, number, number] {
  const t = Math.max(0, Math.min(1, value / 255));
  if (t < 0.34) {
    const k = t / 0.34;
    return [10 + k * 35, 22 + k * 150, 36 + k * 160];
  }

  if (t < 0.68) {
    const k = (t - 0.34) / 0.34;
    return [45 + k * 185, 172 + k * 30, 196 - k * 120];
  }

  const k = (t - 0.68) / 0.32;
  return [230 + k * 25, 202 - k * 95, 76 - k * 40];
}

const COLOR_LUT = (() => {
  const lut = new Uint32Array(256);
  for (let value = 0; value < 256; value++) {
    const [r, g, b] = color(value);
    lut[value] = (255 << 24) | (b << 16) | (g << 8) | r;
  }

  return lut;
})();

function buildOverlay(): HTMLCanvasElement {
  const overlay = document.createElement('canvas');
  overlay.width = WIDTH;
  overlay.height = HEIGHT;
  const context = overlay.getContext('2d');
  if (!context) return overlay;

  context.strokeStyle = 'rgba(255,255,255,0.08)';
  context.lineWidth = 1;
  for (const [frequency] of FREQUENCY_REFS) {
    const y = DATA_HEIGHT - (Math.log(frequency / 20) / Math.log(1000)) * DATA_HEIGHT;
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(WIDTH, y);
    context.stroke();
  }

  context.font = '9px Consolas, monospace';
  context.textBaseline = 'bottom';
  for (const [frequency, label] of FREQUENCY_REFS) {
    const y = DATA_HEIGHT - (Math.log(frequency / 20) / Math.log(1000)) * DATA_HEIGHT;
    const labelY = Math.max(9, Math.min(DATA_HEIGHT - 2, y - 1));
    const width = context.measureText(label).width;
    context.fillStyle = 'rgba(0,0,0,0.72)';
    context.fillRect(1, labelY - 9, width + 5, 10);
    context.fillStyle = 'rgba(255,255,255,0.9)';
    context.fillText(label, 3, labelY);
  }

  return overlay;
}

export default function SpectrogramView({
  values,
  newColumns,
  totalColumns,
  full,
  capacity = 256,
  windowSeconds,
  peakHz,
}: {
  values?: number[];
  newColumns?: number;
  totalColumns?: number;
  full?: boolean;
  capacity?: number;
  windowSeconds?: number;
  peakHz?: number;
}) {
  const { t, language } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const offscreenRef = useRef<HTMLCanvasElement | null>(null);
  const overlayRef = useRef<HTMLCanvasElement | null>(null);
  const overlayLanguageRef = useRef<string>('');
  const historyRef = useRef<Uint8Array | null>(null);
  const capacityRef = useRef(0);
  const lastTotalRef = useRef(-1);

  useEffect(() => {
    const canvas = ref.current;
    const context = canvas?.getContext('2d');
    if (!canvas || !context) return;

    const cap = Math.max(1, Math.floor(capacity));
    let offscreen = offscreenRef.current;
    if (!offscreen || capacityRef.current !== cap) {
      offscreen = document.createElement('canvas');
      offscreen.width = cap;
      offscreen.height = BANDS;
      offscreenRef.current = offscreen;
      historyRef.current = new Uint8Array(cap * BANDS);
      capacityRef.current = cap;
      lastTotalRef.current = -1;
    }

    const history = historyRef.current!;
    const data = values ?? [];
    const total = totalColumns ?? 0;
    const isFull = full === true || lastTotalRef.current < 0 || total < lastTotalRef.current;
    const offContext = offscreen.getContext('2d');
    if (!offContext) return;

    const clampByte = (value: number) => Math.max(0, Math.min(255, Math.round(value)));
    const drawColumns = (columns: number, startColumn: number) => {
      const image = offContext.createImageData(columns, BANDS);
      const pixels = new Uint32Array(image.data.buffer);
      for (let row = 0; row < BANDS; row++) {
        const band = BANDS - 1 - row;
        const rowOffset = row * columns;
        for (let column = 0; column < columns; column++) {
          const value = history[(startColumn + column) * BANDS + band];
          pixels[rowOffset + column] = COLOR_LUT[value];
        }
      }

      offContext.putImageData(image, startColumn, 0);
    };

    if (isFull) {
      history.fill(0);
      const length = Math.min(data.length, cap * BANDS);
      for (let index = 0; index < length; index++) {
        history[index] = clampByte(data[index]);
      }

      drawColumns(cap, 0);
      lastTotalRef.current = total;
    } else {
      const count = Math.max(0, Math.min(newColumns ?? 0, cap));
      if (count > 0) {
        const available = Math.min(count, Math.floor(data.length / BANDS));
        if (available > 0) {
          const samples = available * BANDS;
          if (available < cap) {
            history.copyWithin(0, samples);
            offContext.drawImage(offscreen, -available, 0);
          } else {
            history.fill(0);
          }

          const destination = (cap - available) * BANDS;
          for (let index = 0; index < samples; index++) {
            history[destination + index] = clampByte(data[index]);
          }

          drawColumns(available, cap - available);
        }

        lastTotalRef.current = total;
      }
    }

    context.clearRect(0, 0, WIDTH, HEIGHT);
    context.imageSmoothingEnabled = false;
    context.drawImage(offscreen, 0, 0, cap, BANDS, 0, 0, WIDTH, DATA_HEIGHT);

    if (!overlayRef.current || overlayLanguageRef.current !== language) {
      overlayRef.current = buildOverlay();
      overlayLanguageRef.current = language;
    }

    if (overlayRef.current) {
      context.drawImage(overlayRef.current, 0, 0);
    }
  }, [values, newColumns, totalColumns, full, capacity, language, t]);

  const seconds = windowSeconds && Number.isFinite(windowSeconds)
    ? windowSeconds
    : Math.max(1, capacity) / 31.25;

  return (
    <div className="spectrogram-view">
      <canvas ref={ref} width={WIDTH} height={HEIGHT} aria-label={t('Live spectrogram')} />
      <div className="spectrum-meta">
        <span>{t('{seconds} s window', { seconds: seconds.toFixed(1) })}</span>
        <strong>{formatHz(peakHz)}</strong>
      </div>
    </div>
  );
}
