import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { drawTimeline, type TimelineCanvasParams } from './timelineCanvas';
import type { TimelinePalette } from '../timelinePalette';
import type { TimelineTrackModel } from '../timeline';

interface CanvasCall {
  method: string;
  args: unknown[];
}

function fakeCanvas(): { canvas: HTMLCanvasElement; calls: CanvasCall[] } {
  const calls: CanvasCall[] = [];
  const context = new Proxy({} as CanvasRenderingContext2D, {
    get: (_target, property) => (...args: unknown[]) => {
      calls.push({ method: String(property), args });
    },
    set: () => true,
  });
  const canvas = {
    width: 0,
    height: 0,
    style: {},
    getContext: () => context,
  } as unknown as HTMLCanvasElement;
  return { canvas, calls };
}

const palette = new Proxy({}, { get: () => '#123456' }) as TimelinePalette;

const track: TimelineTrackModel = {
  id: 'track-1',
  name: 'Track',
  gain: 1,
  muted: false,
  solo: false,
  automation: [],
  clips: [{
    id: 'clip-1',
    name: 'Clip',
    sourcePath: '',
    startSample: 0,
    lengthSamples: 48000,
    sourceOffsetSamples: 0,
    gain: 1,
    pitchSemitones: 0,
    fadeInSamples: 0,
    fadeOutSamples: 0,
    loop: false,
    peaks: [0.5],
  }],
};

function params(overrides: Partial<TimelineCanvasParams> = {}): TimelineCanvasParams {
  return {
    width: 800,
    height: 200,
    palette,
    seconds: 4,
    beatSeconds: 0.5,
    beatsPerBar: 4,
    pixelsPerSecond: 100,
    sampleRate: 48000,
    displayLoopActive: false,
    displayLoopStart: 0,
    displayLoopEnd: 0,
    rows: [{ kind: 'track', index: 0, track, y: 22, height: 72 }],
    tracks: [track],
    selectedClipKeys: [],
    selectedClipModel: null,
    drag: null,
    automationDrag: null,
    automationTargets: [],
    markers: [],
    playheadX: 50,
    audioClipLabel: 'Audio clip',
    ...overrides,
  };
}

describe('drawTimeline', () => {
  beforeEach(() => {
    vi.stubGlobal('window', { devicePixelRatio: 1 });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('draws ruler, clip label, marker label and playhead', () => {
    const { canvas, calls } = fakeCanvas();
    drawTimeline(canvas, params({
      markers: [{ id: 'marker-1', name: 'Intro', startSample: 0, endSample: 0 }],
    }));

    expect(canvas.width).toBe(800);
    expect(canvas.height).toBe(200);
    expect(calls.some(call => call.method === 'fillText' && call.args[0] === 'Clip')).toBe(true);
    expect(calls.some(call => call.method === 'fillText' && call.args[0] === 'Intro')).toBe(true);
    expect(calls.some(call => call.method === 'arc')).toBe(true);
    expect(calls.some(call => call.method === 'stroke')).toBe(true);
  });

  it('draws an automation lane curve', () => {
    const { canvas, calls } = fakeCanvas();
    const lane = {
      id: 'lane-1',
      targetNodeId: 'node-1',
      processorIndex: 0,
      parameterIndex: 0,
      points: [
        { sample: 0, value: 0, curve: 0 },
        { sample: 48000, value: 1, curve: 0 },
      ],
    };
    drawTimeline(canvas, params({
      rows: [{ kind: 'automation', index: 0, track, lane, y: 94, height: 48 }],
      automationTargets: [{
        nodeId: 'node-1',
        label: 'Gain',
        processorIndex: 0,
        parameterIndex: 0,
        min: 0,
        max: 1,
        value: 0,
      }],
    }));

    expect(calls.some(call => call.method === 'lineTo')).toBe(true);
    expect(calls.some(call => call.method === 'arc')).toBe(true);
  });

  it('scales the backing store by devicePixelRatio', () => {
    vi.stubGlobal('window', { devicePixelRatio: 2 });
    const { canvas } = fakeCanvas();
    drawTimeline(canvas, params());
    expect(canvas.width).toBe(1600);
    expect(canvas.height).toBe(400);
  });

  it('can skip the playhead for the static canvas layer', () => {
    const { canvas, calls } = fakeCanvas();
    drawTimeline(canvas, params({ drawPlayhead: false }));
    const drawsPlayhead = calls.some(call =>
      call.method === 'lineTo' && call.args[0] === 50 && call.args[1] === 8);
    expect(drawsPlayhead).toBe(false);
  });
});
