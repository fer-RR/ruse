import { describe, expect, it } from 'vitest';
import { chainLayout, clampPositionToRect, audioLinkDirection, isAudioNode } from './layout';
import type { AudioLinkData, ChainLayoutLink, ChainLayoutNode } from './layout';

const nodes: ChainLayoutNode[] = [
  { id: 'mic', kind: 'source' },
  { id: 'effect1', kind: 'modifier' },
  { id: 'effect2', kind: 'modifier' },
  { id: 'effect3', kind: 'modifier' },
  { id: 'effect4', kind: 'modifier' },
  { id: 'output', kind: 'output' },
];

const links: ChainLayoutLink[] = [
  { from: 'mic', to: 'effect1' },
  { from: 'effect1', to: 'effect2' },
  { from: 'effect2', to: 'output' },
  { from: 'mic', to: 'effect3' },
  { from: 'effect3', to: 'effect4' },
  { from: 'effect4', to: 'output' },
];

describe('chainLayout', () => {
  it('places parallel effect chains on separate rows', () => {
    const positions = new Map(chainLayout(nodes, links).map(position => [position.id, position]));

    expect(positions.get('effect1')?.y).toBe(positions.get('mic')?.y);
    expect(positions.get('effect3')?.y).toBeGreaterThan(positions.get('effect1')?.y ?? 0);
    expect(positions.get('effect3')?.x).toBe(positions.get('effect1')?.x);
    expect(positions.get('effect4')?.x).toBe(positions.get('effect2')?.x);
    expect(positions.get('output')?.x).toBeGreaterThan(positions.get('effect2')?.x ?? 0);
    expect(positions.get('output')?.y).toBe(positions.get('mic')?.y);
  });

  it('keeps a shared prefix on one row and branches below it', () => {
    const branchNodes: ChainLayoutNode[] = [
      { id: 'mic', kind: 'source' },
      { id: 'effect1', kind: 'modifier' },
      { id: 'effect2', kind: 'modifier' },
      { id: 'effect3', kind: 'modifier' },
      { id: 'output', kind: 'output' },
    ];
    const branchLinks: ChainLayoutLink[] = [
      { from: 'mic', to: 'effect1' },
      { from: 'effect1', to: 'effect2' },
      { from: 'effect2', to: 'output' },
      { from: 'effect1', to: 'effect3' },
      { from: 'effect3', to: 'output' },
    ];

    const positions = new Map(chainLayout(branchNodes, branchLinks).map(position => [position.id, position]));

    expect(positions.get('effect1')?.y).toBe(positions.get('mic')?.y);
    expect(positions.get('effect2')?.x).toBe(positions.get('effect3')?.x);
    expect(positions.get('effect3')?.y).toBeGreaterThan(positions.get('effect2')?.y ?? 0);
    expect(positions.get('output')?.y).toBe(positions.get('mic')?.y);
  });
});

describe('clampPositionToRect', () => {
  const rect = { x: 0, y: 0, width: 800, height: 600 };

  it('keeps a normal node unchanged', () => {
    expect(clampPositionToRect({ x: 100, y: 80 }, 200, 120, rect)).toEqual({ x: 100, y: 80 });
  });

  it('clamps above the visible canvas', () => {
    expect(clampPositionToRect({ x: 100, y: -216 }, 200, 120, rect)).toEqual({ x: 100, y: 0 });
  });

  it('clamps all four edges', () => {
    expect(clampPositionToRect({ x: -50, y: -20 }, 200, 120, rect)).toEqual({ x: 0, y: 0 });
    expect(clampPositionToRect({ x: 700, y: 550 }, 200, 120, rect)).toEqual({ x: 600, y: 480 });
  });

  it('pins an oversized node to the top-left', () => {
    expect(clampPositionToRect({ x: 300, y: 300 }, 900, 700, rect)).toEqual({ x: 0, y: 0 });
  });
});

const node = (id: string, kind: AudioLinkData['kind'], extra: Partial<AudioLinkData> = {}) =>
  ({ id, kind, ...extra } as AudioLinkData);

describe('audioLinkDirection', () => {
  it('connects a source onto an output', () => {
    expect(audioLinkDirection(node('s', 'source'), node('o', 'output'))).toEqual({ from: 's', to: 'o' });
  });

  it('reverses when the moved node is the sink', () => {
    expect(audioLinkDirection(node('o', 'output'), node('s', 'source'))).toEqual({ from: 's', to: 'o' });
  });

  it('chains modifiers using the moved node as the source', () => {
    expect(audioLinkDirection(node('m1', 'modifier'), node('m2', 'modifier'))).toEqual({ from: 'm1', to: 'm2' });
  });

  it('routes a source through a dropped modifier', () => {
    expect(audioLinkDirection(node('m', 'modifier'), node('s', 'source'))).toEqual({ from: 's', to: 'm' });
  });

  it('treats a combined network node as both ends', () => {
    const network = node('n', 'network', { isNetworkInput: true, isNetworkOutput: true });
    expect(audioLinkDirection(network, node('s', 'source'))).toEqual({ from: 's', to: 'n' });
  });

  it('rejects notes, diagnostics and same-node pairs', () => {
    expect(audioLinkDirection(node('s', 'source'), node('t', 'text', { isTextNote: true }))).toBeNull();
    expect(audioLinkDirection(node('s', 'source'), node('s', 'source'))).toBeNull();
    expect(isAudioNode(node('t', 'text', { isTextNote: true }))).toBe(false);
    expect(isAudioNode(node('d', 'modifier', { diagnostics: true }))).toBe(false);
    expect(isAudioNode(node('o', 'output'))).toBe(true);
  });
});
