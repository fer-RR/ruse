import { describe, expect, it } from 'vitest';
import { chainLayout } from './layout';
import type { ChainLayoutLink, ChainLayoutNode } from './layout';

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
