import { HEARING_BANDS } from './hearingBands';

export type Ear = 'left' | 'right';

export { HEARING_BANDS };
const PTA_BANDS = [500, 1000, 2000, 4000] as const;
const MIN_FREQUENCY = 125;
const MAX_FREQUENCY = 12000;
const MIN_DB_HL = -20;
const MAX_DB_HL = 140;

export interface AudiogramPoint {
  frequency: number;
  dbHl: number;
}

export interface AudiogramData {
  left: AudiogramPoint[];
  right: AudiogramPoint[];
  format: 'csv' | 'json';
  fileName: string;
  subject?: string;
  source?: string;
  notes?: string;
  warnings: string[];
}

export interface EarProfile {
  points: AudiogramPoint[];
  thresholds: number[];
  totalGains: number[];
  bandGains: number[];
  appliedGains: number[];
  perEarGain: number;
  pta: number | null;
  severity: string;
}

export interface HearingProfile {
  left: EarProfile | null;
  right: EarProfile | null;
  warnings: string[];
}

export class AudiogramParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'AudiogramParseError';
  }
}

const FREQUENCY_KEYS = new Set([
  'frequency', 'freq', 'freqhz', 'frequencyhz', 'hz', 'testfrequency', 'soundfreq', 'soundfrequency',
  'frequence', 'frequenz', 'frecuencia', 'frequenza', 'frequencia',
]);
const EAR_KEYS = new Set(['ear', 'side', 'testear', 'earside', 'oreille', 'ohr', 'oido', 'orecchio', 'ouvido']);
const THRESHOLD_KEYS = new Set([
  'dbhl', 'db', 'threshold', 'thresholddbhl', 'hearinglevel', 'hearingleveldbhl', 'level', 'thresholdlevel', 'dbhlthreshold',
  'seuil', 'seuilentendu', 'horschwelle', 'horchwelle', 'schwelle', 'umbral', 'soglia', 'limiar', 'nivel', 'pegel',
]);
const LEFT_KEYS = new Set([
  'left', 'leftear', 'lear', 'leftdb', 'leftdbhl', 'dbhlleft', 'leftthreshold', 'leftthresholddbhl', 'lefteardbhl', 'lefteardb', 'leftearthreshold', 'leftearthresholddbhl', 'ldbhl', 'lt', 'l',
  'gauche', 'oreillegauche', 'links', 'ohrlinks', 'izquierdo', 'oidoizquierdo', 'sinistro', 'orecchiosinistro', 'esquerdo', 'ouvidoesquerdo',
]);
const RIGHT_KEYS = new Set([
  'right', 'rightear', 'rear', 'rightdb', 'rightdbhl', 'dbhlright', 'rightthreshold', 'rightthresholddbhl', 'righteardbhl', 'righteardb', 'rightearthreshold', 'rightearthresholddbhl', 'rdbhl', 'rt', 'r',
  'droite', 'oreilledroite', 'rechts', 'ohrrechts', 'derecho', 'oidoderecho', 'destro', 'orecchiodestro', 'direito', 'ouvidadireito',
]);

function normalizeKey(value: string): string {
  return value
    .replace(/^\uFEFF/, '')
    .trim()
    .toLowerCase()
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9.]/g, '');
}

function normalizeEar(value: string): Ear | null {
  const key = normalizeKey(value);
  if (LEFT_KEYS.has(key) || key === 'leftear') return 'left';
  if (RIGHT_KEYS.has(key) || key === 'rightear') return 'right';
  return null;
}

export function parseFrequency(value: string): number | null {
  const text = value.replace(/^\uFEFF/, '').trim().toLowerCase().replace(/,/g, '.');
  if (!text) return null;

  const match = text.match(/^(-?\d+(?:\.\d+)?)\s*(k|khz|hz)?$/);
  if (!match) return null;

  let amount = Number(match[1]);
  if (!Number.isFinite(amount)) return null;

  const unit = match[2];
  if (unit === 'k' || unit === 'khz') {
    amount *= 1000;
  } else if (!unit && amount > 0 && amount < 20) {
    amount *= 1000;
  }

  return amount >= 20 && amount <= 20000 ? amount : null;
}

function parseThreshold(value: string): { value: number; warning?: string } | null {
  const text = value.replace(/^\uFEFF/, '').trim();
  if (!text) return null;

  const lower = text.toLowerCase();
  if (/^(nr|no response|no resp|none|absent|>|>120|≥|>=)/.test(lower)) {
    return { value: 120, warning: 'No-response threshold treated as 120 dB HL.' };
  }

  const match = text.replace(/,/g, '.').match(/-?\d+(?:\.\d+)?/);
  if (!match) return null;

  const amount = Number(match[0]);
  if (!Number.isFinite(amount)) return null;
  if (amount < MIN_DB_HL || amount > MAX_DB_HL) {
    return {
      value: Math.max(MIN_DB_HL, Math.min(MAX_DB_HL, amount)),
      warning: `Threshold ${amount} dB HL was clamped to the supported range.`,
    };
  }

  return { value: amount };
}

function parseEarValue(value: string): Ear | null {
  return normalizeEar(value);
}

const EAR_TOKENS = [
  ...Array.from(LEFT_KEYS).filter(token => token.length >= 4).map(token => ({ token, ear: 'left' as Ear })),
  ...Array.from(RIGHT_KEYS).filter(token => token.length >= 4).map(token => ({ token, ear: 'right' as Ear })),
].sort((a, b) => b.token.length - a.token.length);

function parseEarFrequencyHeader(value: string): { ear: Ear; frequency: number } | null {
  const key = normalizeKey(value);
  if (!key) return null;
  const token = EAR_TOKENS.find(item => key.includes(item.token));
  if (!token) return null;
  const remaining = key.replace(token.token, '');
  const match = remaining.match(/(\d+(?:[.,]\d+)?)\s*(k|khz|hz)?/i);
  if (!match) return null;
  if (match[1] === '05') return { ear: token.ear, frequency: 500 };
  const frequency = parseFrequency(match[0]);
  return frequency == null ? null : { ear: token.ear, frequency };
}

function parsePointObject(value: unknown): AudiogramPoint | null {
  if (!value || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const rawFrequency = record.freq_hz ?? record.frequency ?? record.freq ?? record.hz ?? record.frequency_hz;
  const rawThreshold = record.db_hl ?? record.dbhl ?? record.threshold ?? record.db ?? record.level ?? record.hearing_level;
  if (rawFrequency == null || rawThreshold == null) return null;
  const frequency = parseFrequency(String(rawFrequency));
  const threshold = parseThreshold(String(rawThreshold));
  if (frequency == null || threshold == null) return null;
  return { frequency, dbHl: threshold.value };
}

function parseEarObject(value: unknown): AudiogramPoint[] {
  if (!value || typeof value !== 'object') return [];
  const record = value as Record<string, unknown>;
  const thresholds = record.thresholds;
  if (Array.isArray(thresholds)) {
    return thresholds.map(parsePointObject).filter((point): point is AudiogramPoint => point != null);
  }

  if (thresholds && typeof thresholds === 'object') {
    return Object.entries(thresholds as Record<string, unknown>)
      .map(([frequency, db]) => {
        const parsedFrequency = parseFrequency(frequency);
        const parsedThreshold = parseThreshold(String(db));
        if (parsedFrequency == null || parsedThreshold == null) return null;
        return { frequency: parsedFrequency, dbHl: parsedThreshold.value };
      })
      .filter((point): point is AudiogramPoint => point != null);
  }

  if (Array.isArray(value)) {
    return value.map(parsePointObject).filter((point): point is AudiogramPoint => point != null);
  }

  return [];
}

function parseJsonEarValue(value: unknown): AudiogramPoint[] {
  if (Array.isArray(value)) {
    return value.map(parsePointObject).filter((point): point is AudiogramPoint => point != null);
  }

  if (value && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const fromObject = parseEarObject(record);
    if (fromObject.length > 0) return fromObject;

    return Object.entries(record)
      .map(([frequency, db]) => {
        const parsedFrequency = parseFrequency(frequency);
        const parsedThreshold = parseThreshold(String(db));
        if (parsedFrequency == null || parsedThreshold == null) return null;
        return { frequency: parsedFrequency, dbHl: parsedThreshold.value };
      })
      .filter((point): point is AudiogramPoint => point != null);
  }

  return [];
}

function parseJson(text: string, fileName: string): AudiogramData {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (error) {
    throw new AudiogramParseError(error instanceof Error ? `Invalid JSON: ${error.message}` : 'Invalid JSON.');
  }

  let left: AudiogramPoint[] = [];
  let right: AudiogramPoint[] = [];
  let subject: string | undefined;
  let source: string | undefined;
  let notes: string | undefined;
  const warnings: string[] = [];

  if (value && typeof value === 'object' && !Array.isArray(value)) {
    const record = value as Record<string, unknown>;
    subject = typeof record.subject === 'string' ? record.subject : undefined;
    source = typeof record.source === 'string' ? record.source : undefined;
    notes = typeof record.notes === 'string' ? record.notes : undefined;

    if (record.right_ear || record.rightEar || record.left_ear || record.leftEar) {
      right = parseEarObject(record.right_ear ?? record.rightEar);
      left = parseEarObject(record.left_ear ?? record.leftEar);
    } else if (record.frequencies && (record.left != null || record.right != null)) {
      const frequencies = Array.isArray(record.frequencies)
        ? record.frequencies.map(item => parseFrequency(String(item))).filter((item): item is number => item != null)
        : [];
      const leftValues = Array.isArray(record.left) ? record.left : [];
      const rightValues = Array.isArray(record.right) ? record.right : [];
      left = frequencies.map((frequency, index) => {
        const parsed = parseThreshold(String(leftValues[index] ?? ''));
        return parsed ? { frequency, dbHl: parsed.value } : null;
      }).filter((point): point is AudiogramPoint => point != null);
      right = frequencies.map((frequency, index) => {
        const parsed = parseThreshold(String(rightValues[index] ?? ''));
        return parsed ? { frequency, dbHl: parsed.value } : null;
      }).filter((point): point is AudiogramPoint => point != null);
    } else {
      left = parseJsonEarValue(record.left ?? record.left_ear ?? record.leftEar);
      right = parseJsonEarValue(record.right ?? record.right_ear ?? record.rightEar);
    }
  } else if (Array.isArray(value)) {
    for (const item of value) {
      if (!item || typeof item !== 'object') continue;
      const record = item as Record<string, unknown>;
      const ear = parseEarValue(String(record.ear ?? record.side ?? ''));
      const point = parsePointObject(record);
      if (!ear || !point) continue;
      if (ear === 'left') left.push(point); else right.push(point);
    }
  }

  if (left.length === 0 && right.length === 0) {
    throw new AudiogramParseError('No audiogram thresholds were found. Use a JSON schema with left/right thresholds or a CSV with frequency and left/right columns.');
  }

  return normalizeAudiogram({ left, right, format: 'json', fileName, subject, source, notes, warnings });
}

function splitDelimited(text: string, delimiter: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let quoted = false;

  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (quoted) {
      if (character === '"') {
        if (text[index + 1] === '"') {
          field += '"';
          index++;
        } else {
          quoted = false;
        }
      } else {
        field += character;
      }
    } else if (character === '"') {
      quoted = true;
    } else if (character === delimiter) {
      row.push(field);
      field = '';
    } else if (character === '\n') {
      row.push(field);
      rows.push(row);
      row = [];
      field = '';
    } else if (character !== '\r') {
      field += character;
    }
  }

  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows;
}

function detectDelimiter(text: string): string {
  const firstLine = text.split(/\r?\n/).find(line => line.trim().length > 0) ?? '';
  const candidates = [',', ';', '\t'];
  let best = ',';
  let bestCount = -1;
  for (const candidate of candidates) {
    const count = firstLine.split(candidate).length - 1;
    if (count > bestCount) {
      best = candidate;
      bestCount = count;
    }
  }
  return best;
}

function parseCsv(text: string, fileName: string): AudiogramData {
  const delimiter = detectDelimiter(text);
  const rows = splitDelimited(text.replace(/^\uFEFF/, ''), delimiter)
    .filter(row => row.some(field => field.trim().length > 0 && !field.trim().startsWith('#')));
  if (rows.length < 2) {
    throw new AudiogramParseError('The CSV file does not contain a header and at least one data row.');
  }

  const header = rows[0].map(normalizeKey);
  const dataRows = rows.slice(1);
  const left: AudiogramPoint[] = [];
  const right: AudiogramPoint[] = [];
  const warnings: string[] = [];

  const frequencyIndex = header.findIndex(key => FREQUENCY_KEYS.has(key));
  const earIndex = header.findIndex(key => EAR_KEYS.has(key));
  const thresholdIndex = header.findIndex(key => THRESHOLD_KEYS.has(key));
  const leftIndex = header.findIndex(key => LEFT_KEYS.has(key));
  const rightIndex = header.findIndex(key => RIGHT_KEYS.has(key));

  const addPoint = (ear: Ear, point: AudiogramPoint | null) => {
    if (!point) return;
    if (ear === 'left') left.push(point); else right.push(point);
  };

  if (earIndex >= 0 && thresholdIndex >= 0) {
    const frequencyHeaders = header
      .map((key, index) => ({ key, index, frequency: parseFrequency(rows[0][index]) }))
      .filter(item => item.frequency != null && item.index !== earIndex);

    if (frequencyIndex >= 0) {
      for (const row of dataRows) {
        const ear = parseEarValue(row[earIndex] ?? '');
        const frequency = parseFrequency(row[frequencyIndex] ?? '');
        const threshold = parseThreshold(row[thresholdIndex] ?? '');
        if (!ear || frequency == null || !threshold) continue;
        if (threshold.warning) warnings.push(threshold.warning);
        addPoint(ear, { frequency, dbHl: threshold.value });
      }
    } else if (frequencyHeaders.length > 0) {
      for (const row of dataRows) {
        const ear = parseEarValue(row[earIndex] ?? '');
        if (!ear) continue;
        for (const { index, frequency } of frequencyHeaders) {
          if (frequency == null) continue;
          const threshold = parseThreshold(row[index] ?? '');
          if (!threshold) continue;
          if (threshold.warning) warnings.push(threshold.warning);
          addPoint(ear, { frequency, dbHl: threshold.value });
        }
      }
    }
  }

  if (left.length === 0 && right.length === 0 && earIndex < 0 && frequencyIndex >= 0 && (leftIndex >= 0 || rightIndex >= 0)) {
    for (const row of dataRows) {
      const frequency = parseFrequency(row[frequencyIndex] ?? '');
      if (frequency == null) continue;
      if (leftIndex >= 0) {
        const threshold = parseThreshold(row[leftIndex] ?? '');
        if (threshold) {
          if (threshold.warning) warnings.push(threshold.warning);
          addPoint('left', { frequency, dbHl: threshold.value });
        }
      }
      if (rightIndex >= 0) {
        const threshold = parseThreshold(row[rightIndex] ?? '');
        if (threshold) {
          if (threshold.warning) warnings.push(threshold.warning);
          addPoint('right', { frequency, dbHl: threshold.value });
        }
      }
    }
  }

  if (left.length === 0 && right.length === 0) {
    const earHeaderIndex = header.findIndex(key => EAR_KEYS.has(key));
    const frequencyHeaders = header
      .map((key, index) => ({ key, index, frequency: parseFrequency(rows[0][index]) }))
      .filter(item => item.frequency != null);

    if (earHeaderIndex >= 0 && frequencyHeaders.length > 0) {
      for (const row of dataRows) {
        const ear = parseEarValue(row[earHeaderIndex] ?? '');
        if (!ear) continue;
        for (const { index, frequency } of frequencyHeaders) {
          if (frequency == null) continue;
          const threshold = parseThreshold(row[index] ?? '');
          if (!threshold) continue;
          if (threshold.warning) warnings.push(threshold.warning);
          addPoint(ear, { frequency, dbHl: threshold.value });
        }
      }
    }
  }

  if (left.length === 0 && right.length === 0) {
    const earFrequencyColumns = header
      .map((_key, index) => {
        const parsed = parseEarFrequencyHeader(rows[0][index]);
        return parsed == null ? null : { ...parsed, index };
      })
      .filter((item): item is { ear: Ear; frequency: number; index: number } => item != null);

    for (const row of dataRows) {
      for (const { ear, frequency, index } of earFrequencyColumns) {
        const threshold = parseThreshold(row[index] ?? '');
        if (!threshold) continue;
        if (threshold.warning) warnings.push(threshold.warning);
        addPoint(ear, { frequency, dbHl: threshold.value });
      }
    }
  }

  if (left.length === 0 && right.length === 0) {
    throw new AudiogramParseError('Could not identify frequency and left/right threshold columns. Use frequency,left,right or ear,frequency,db_hl.');
  }

  return normalizeAudiogram({ left, right, format: 'csv', fileName, warnings });
}

function normalizePoints(points: AudiogramPoint[], warnings: string[], ear: Ear): AudiogramPoint[] {
  const sorted = [...points]
    .filter(point => Number.isFinite(point.frequency) && Number.isFinite(point.dbHl))
    .sort((a, b) => a.frequency - b.frequency);
  const normalized: AudiogramPoint[] = [];
  for (const point of sorted) {
    if (point.frequency < MIN_FREQUENCY || point.frequency > MAX_FREQUENCY) {
      warnings.push(`${ear} ${Math.round(point.frequency)} Hz is outside the supported 125-12000 Hz range and was skipped.`);
      continue;
    }
    const previous = normalized[normalized.length - 1];
    if (previous && Math.abs(previous.frequency - point.frequency) < 0.1) {
      previous.dbHl = point.dbHl;
      warnings.push(`Duplicate ${ear} ${Math.round(point.frequency)} Hz threshold used the last value.`);
    } else {
      normalized.push({ ...point });
    }
  }
  return normalized;
}

function normalizeAudiogram(input: AudiogramData): AudiogramData {
  const warnings = [...input.warnings];
  const left = normalizePoints(input.left, warnings, 'left');
  const right = normalizePoints(input.right, warnings, 'right');
  if (left.length === 0 && right.length === 0) {
    throw new AudiogramParseError('No usable thresholds remained after validation.');
  }
  return { ...input, left, right, warnings };
}

export function parseAudiogram(text: string, fileName: string): AudiogramData {
  const trimmed = text.trim();
  if (!trimmed) throw new AudiogramParseError('The selected file is empty.');

  const looksLikeJson = trimmed.startsWith('{') || trimmed.startsWith('[') || fileName.toLowerCase().endsWith('.json');
  return looksLikeJson ? parseJson(trimmed, fileName) : parseCsv(trimmed, fileName);
}

function interpolate(points: AudiogramPoint[], frequency: number): number {
  if (points.length === 0) return 0;
  if (frequency <= points[0].frequency) return points[0].dbHl;
  const last = points[points.length - 1];
  if (frequency >= last.frequency) return last.dbHl;

  for (let index = 0; index < points.length - 1; index++) {
    const lower = points[index];
    const upper = points[index + 1];
    if (frequency < lower.frequency || frequency > upper.frequency) continue;

    const span = Math.log(upper.frequency / lower.frequency);
    if (span <= 0) return lower.dbHl;
    const ratio = Math.log(frequency / lower.frequency) / span;
    return lower.dbHl + (upper.dbHl - lower.dbHl) * ratio;
  }

  return last.dbHl;
}

export function interpolateThresholds(points: AudiogramPoint[], frequencies: readonly number[] = HEARING_BANDS): number[] {
  const sorted = [...points].sort((a, b) => a.frequency - b.frequency);
  return frequencies.map(frequency => Math.round(interpolate(sorted, frequency) * 10) / 10);
}

export function halfGainForFrequency(thresholdDbHl: number, frequency: number): number {
  if (!Number.isFinite(thresholdDbHl) || thresholdDbHl <= 25) return 0;
  let gain = thresholdDbHl / 2;
  if (frequency <= 250) gain -= 10;
  else if (frequency <= 500) gain -= 5;
  return Math.round(Math.max(0, gain) * 2) / 2;
}

function pta(points: AudiogramPoint[]): number | null {
  if (points.length === 0) return null;
  const values = interpolateThresholds(points, PTA_BANDS);
  return Math.round((values.reduce((sum, value) => sum + value, 0) / values.length) * 10) / 10;
}

export function severityForPta(value: number | null): string {
  if (value == null || !Number.isFinite(value)) return 'Unknown';
  if (value <= 25) return 'Normal';
  if (value <= 40) return 'Mild';
  if (value <= 55) return 'Moderate';
  if (value <= 70) return 'Moderately severe';
  if (value <= 90) return 'Severe';
  return 'Profound';
}

function buildEar(points: AudiogramPoint[], ear: Ear, warnings: string[]): EarProfile | null {
  if (points.length === 0) return null;
  if (points.length < 3) {
    warnings.push(`${ear === 'left' ? 'Left' : 'Right'} ear has only ${points.length} measured frequency${points.length === 1 ? '' : 'ies'}; interpolation is coarse.`);
  }

  const thresholds = interpolateThresholds(points);
  const totalGains = thresholds.map((threshold, index) => halfGainForFrequency(threshold, HEARING_BANDS[index]));
  if (Math.max(...totalGains) > 48) {
    warnings.push(`${ear === 'left' ? 'Left' : 'Right'} ear needs more than +48 dB in one band; the node limit will clip the profile.`);
  }
  if (thresholds.some(threshold => threshold > 90)) {
    warnings.push(`${ear === 'left' ? 'Left' : 'Right'} ear has severe/profound thresholds; this is a starting point, not a clinical fitting.`);
  }

  const meanGain = totalGains.reduce((sum, gain) => sum + gain, 0) / totalGains.length;
  const perEarGain = Math.round(Math.max(-24, Math.min(24, meanGain)));
  const bandGains = totalGains.map(gain => Math.round(Math.max(-24, Math.min(24, gain - perEarGain)) * 2) / 2);
  const appliedGains = bandGains.map(gain => gain + perEarGain);
  const ptaValue = pta(points);
  if (ptaValue != null && ptaValue > 70) {
    warnings.push(`${ear === 'left' ? 'Left' : 'Right'} ear PTA4 is ${ptaValue.toFixed(1)} dB HL (severe/profound); professional fitting is strongly recommended.`);
  }

  return {
    points,
    thresholds,
    totalGains,
    bandGains,
    appliedGains,
    perEarGain,
    pta: ptaValue,
    severity: severityForPta(ptaValue),
  };
}

export function buildHearingProfile(audiogram: AudiogramData): HearingProfile {
  const warnings = [...audiogram.warnings];
  const left = buildEar(audiogram.left, 'left', warnings);
  const right = buildEar(audiogram.right, 'right', warnings);

  if (!left) warnings.push('No left-ear thresholds found; the left profile will not be changed.');
  if (!right) warnings.push('No right-ear thresholds found; the right profile will not be changed.');
  if (left?.pta != null && right?.pta != null && Math.abs(left.pta - right.pta) >= 15) {
    warnings.push(`Asymmetric hearing loss: PTA4 differs by ${Math.abs(left.pta - right.pta).toFixed(1)} dB between ears. Consider professional review.`);
  }

  return { left, right, warnings: [...new Set(warnings)] };
}
