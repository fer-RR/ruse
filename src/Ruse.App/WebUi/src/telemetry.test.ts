import { describe, expect, it } from 'vitest';
import { sameNode } from './telemetry';
import type { NodeTelemetry } from './types';

function node(overrides: Partial<NodeTelemetry> = {}): NodeTelemetry {
  return { level: 0.25, clipping: false, ...overrides };
}

describe('sameNode', () => {
  it('notifies when loudness values change', () => {
    const previous = node({ loudnessIntegrated: -24, loudnessShortTerm: -20 });
    const next = node({ loudnessIntegrated: -23, loudnessShortTerm: -20 });
    expect(sameNode(previous, next)).toBe(false);
  });

  it('does not notify when only the object identity changes', () => {
    const previous = node({ loudnessIntegrated: -24, loudnessRange: 6, loudnessTruePeak: -1 });
    expect(sameNode(previous, { ...previous })).toBe(true);
  });
});
