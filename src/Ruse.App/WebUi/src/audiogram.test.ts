import { describe, expect, it } from 'vitest';
import {
  AudiogramParseError,
  buildHearingProfile,
  halfGainForFrequency,
  interpolateThresholds,
  parseAudiogram,
  severityForPta,
} from './audiogram';

describe('audiogram import', () => {
  it('parses wide CSV with frequency, left and right columns', () => {
    const data = parseAudiogram(`frequency,left,right
125,20,15
250,30,25
500,40,35
1000,45,40
2000,50,45
4000,55,50
8000,60,55`, 'audiogram.csv');

    expect(data.format).toBe('csv');
    expect(data.left).toHaveLength(7);
    expect(data.right).toHaveLength(7);
    expect(data.left[0]).toEqual({ frequency: 125, dbHl: 20 });

    const profile = buildHearingProfile(data);
    expect(profile.left?.pta).toBe(47.5);
    expect(profile.left?.severity).toBe('Moderate');
    expect(profile.right?.pta).toBe(42.5);
    expect(profile.right?.severity).toBe('Moderate');
  });

  it('parses long CSV with ear, frequency and threshold columns', () => {
    const data = parseAudiogram(`ear,frequency,db_hl
left,250,30
right,250,20
left,1000,45
right,1000,35
left,4000,55
right,4000,45`, 'long.csv');

    expect(data.left).toHaveLength(3);
    expect(data.right).toHaveLength(3);
    expect(data.left.find(point => point.frequency === 1000)?.dbHl).toBe(45);
  });

  it('parses wide CSV with one row per ear', () => {
    const data = parseAudiogram(`ear,250,500,1000,2000,4000,8000
left,30,40,45,50,55,60
right,20,30,35,40,45,50`, 'ears.csv');

    expect(data.left).toHaveLength(6);
    expect(data.right).toHaveLength(6);
    expect(data.left.find(point => point.frequency === 4000)?.dbHl).toBe(55);
  });

  it('parses OpenHear JSON', () => {
    const data = parseAudiogram(JSON.stringify({
      format_version: 'openhear-audiogram-v1',
      subject: 'Test',
      right_ear: { thresholds: [{ freq_hz: 500, db_hl: 20 }, { freq_hz: 4000, db_hl: 50 }] },
      left_ear: { thresholds: [{ freq_hz: 500, db_hl: 30 }, { freq_hz: 4000, db_hl: 60 }] },
    }), 'openhear.json');

    expect(data.format).toBe('json');
    expect(data.subject).toBe('Test');
    expect(data.left).toHaveLength(2);
    expect(data.right).toHaveLength(2);
  });

  it('parses simple JSON with frequencies and arrays', () => {
    const data = parseAudiogram(JSON.stringify({
      unit: 'db_hl',
      frequencies: [250, 500, 1000, 2000, 4000, 8000],
      left: [30, 40, 45, 50, 55, 60],
      right: [20, 30, 35, 40, 45, 50],
    }), 'simple.json');

    expect(data.left).toHaveLength(6);
    expect(data.right).toHaveLength(6);
  });

  it('parses JSON ear objects keyed by frequency', () => {
    const data = parseAudiogram(JSON.stringify({
      left: { '250': 30, '1000': 45, '4000': 55 },
      right: { '250': 20, '1000': 35, '4000': 45 },
    }), 'object.json');

    expect(data.left.find(point => point.frequency === 1000)?.dbHl).toBe(45);
    expect(data.right.find(point => point.frequency === 4000)?.dbHl).toBe(45);
  });

  it('parses clinical wide CSV with ear names in headers', () => {
    const data = parseAudiogram(`Frequency (Hz),Left Ear (dB HL),Right Ear (dB HL)
1000,45,35`, 'clinical.csv');
    expect(data.left[0]).toEqual({ frequency: 1000, dbHl: 45 });
    expect(data.right[0]).toEqual({ frequency: 1000, dbHl: 35 });
  });

  it('parses wide CSV with frequency and ear in each header', () => {
    const data = parseAudiogram(`Left 500 Hz,Right 500 Hz,Left 1 kHz,Right 1 kHz
45,35,50,40`, 'wide-ears.csv');
    expect(data.left.find(point => point.frequency === 500)?.dbHl).toBe(45);
    expect(data.right.find(point => point.frequency === 500)?.dbHl).toBe(35);
    expect(data.left.find(point => point.frequency === 1000)?.dbHl).toBe(50);
    expect(data.right.find(point => point.frequency === 1000)?.dbHl).toBe(40);
  });

  it('parses localized semicolon CSV with decimal commas', () => {
    const data = parseAudiogram(`Fréquence;Oreille gauche;Oreille droite
250;30,5;20,0
1000;45,5;35,0`, 'fr.csv');
    expect(data.left.find(point => point.frequency === 1000)?.dbHl).toBe(45.5);
    expect(data.right.find(point => point.frequency === 1000)?.dbHl).toBe(35);
  });

  it('treats NR as 120 dB HL', () => {
    const data = parseAudiogram(`frequency,left,right
1000,NR,20`, 'nr.csv');
    expect(data.left[0].dbHl).toBe(120);
    expect(data.warnings.some(warning => warning.includes('No-response'))).toBe(true);
  });

  it('interpolates in log frequency and applies the half-gain rule', () => {
    const points = [{ frequency: 250, dbHl: 20 }, { frequency: 8000, dbHl: 60 }];
    const interpolated = interpolateThresholds(points, [1000]);
    expect(interpolated[0]).toBeGreaterThan(20);
    expect(interpolated[0]).toBeLessThan(60);

    expect(halfGainForFrequency(20, 1000)).toBe(0);
    expect(halfGainForFrequency(60, 1000)).toBe(30);
    expect(halfGainForFrequency(60, 500)).toBe(25);
    expect(halfGainForFrequency(60, 250)).toBe(20);
  });

  it('classifies PTA severity', () => {
    expect(severityForPta(20)).toBe('Normal');
    expect(severityForPta(30)).toBe('Mild');
    expect(severityForPta(45)).toBe('Moderate');
    expect(severityForPta(60)).toBe('Moderately severe');
    expect(severityForPta(80)).toBe('Severe');
    expect(severityForPta(100)).toBe('Profound');
  });

  it('warns about asymmetric hearing loss', () => {
    const data = parseAudiogram(`frequency,left,right
250,60,20
500,60,20
1000,60,20
2000,60,20
4000,60,20
8000,60,20`, 'asymmetric.csv');
    const profile = buildHearingProfile(data);
    expect(profile.warnings.some(warning => warning.includes('Asymmetric'))).toBe(true);
  });

  it('rejects files without usable thresholds', () => {
    expect(() => parseAudiogram('hello,world', 'bad.csv')).toThrow(AudiogramParseError);
  });
});
