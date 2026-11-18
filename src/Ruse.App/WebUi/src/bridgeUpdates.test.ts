import { describe, expect, it } from 'vitest';
import {
  applyLinkState,
  applyMixerInputStateToFlow,
  applyMixerInputStateToGraph,
  applyMixerStateToFlow,
  applyMixerStateToGraph,
  applyNodeStateToFlow,
  applyNodeStateToGraph,
  applyParameterUpdate,
  applySourceState,
} from './bridgeUpdates';
import type { RuseFlowNode } from './flow';
import type { GraphSnapshot, LinkModel, MixerInputModel, NodeModel } from './types';

function node(id: string, mixerInputs?: MixerInputModel[]): RuseFlowNode {
  return {
    id,
    type: 'ruseNode',
    position: { x: 0, y: 0 },
    data: {
      id,
      kind: 'modifier',
      flavor: '',
      title: id,
      subtitle: '',
      accent: '',
      x: 0,
      y: 0,
      volume: 1,
      muted: false,
      parameters: [{ processorIndex: 0, parameterIndex: 0, name: 'Gain', min: 0, max: 1, value: 1, unit: '', logarithmic: false, isToggle: false, isInteger: false, isChoice: false, options: [] }],
      isRecorder: false,
      recordingPath: '',
      recordingFormat: '',
      highlightMuted: false,
      mixerInputs,
    },
  };
}

function model(id: string): NodeModel {
  return node(id).data as NodeModel;
}

function link(id: string): LinkModel {
  return { id, from: 'a', to: 'b', kind: 'direct', gain: 1, enabled: true };
}

function graph(): GraphSnapshot {
  return { version: 1, running: true, nodes: [model('a'), model('b')], links: [link('l1'), link('l2')] };
}

const input = (linkId: string, gain: number): MixerInputModel => ({
  linkId,
  title: linkId,
  gain,
  pan: 0,
  delayMs: 0,
  muted: false,
  solo: false,
  busB: false,
  inverted: false,
});

describe('bridge updates', () => {
  it('keeps unrelated node identity when a node-state delta arrives', () => {
    const first = node('a');
    const second = node('b');
    const next = applyNodeStateToFlow([first, second], { id: 'a', volume: 0.5 });
    expect(next[0]).not.toBe(first);
    expect(next[0].data.volume).toBe(0.5);
    expect(next[1]).toBe(second);
  });

  it('updates graph node state without rebuilding unrelated nodes', () => {
    const current = graph();
    const next = applyNodeStateToGraph(current, {
      id: 'a',
      muted: true,
      accent: '#fff',
      groupGain: 0.75,
      groupMuted: true,
      groupSolo: false,
    });
    expect(next.nodes[0].muted).toBe(true);
    expect(next.nodes[0].accent).toBe('#fff');
    expect(next.nodes[0].groupGain).toBe(0.75);
    expect(next.nodes[0].groupMuted).toBe(true);
    expect(next.nodes[0].groupSolo).toBe(false);
    expect(next.nodes[1]).toBe(current.nodes[1]);
  });

  it('updates mixer settings on the matching flow node only', () => {
    const first = node('mixer');
    const second = node('other');
    const next = applyMixerStateToFlow([first, second], {
      id: 'mixer',
      width: 1.4,
      autoGain: true,
      crossfade: 0.25,
    });
    expect(next[0].data.mixerWidth).toBe(1.4);
    expect(next[0].data.mixerAutoGain).toBe(true);
    expect(next[0].data.mixerCrossfade).toBe(0.25);
    expect(next[1]).toBe(second);
  });

  it('updates mixer settings on the matching graph node only', () => {
    const current = graph();
    const next = applyMixerStateToGraph(current, {
      id: 'a',
      width: 0.5,
      autoGain: false,
      crossfade: 1,
    });
    expect(next.nodes[0].mixerWidth).toBe(0.5);
    expect(next.nodes[0].mixerCrossfade).toBe(1);
    expect(next.nodes[1]).toBe(current.nodes[1]);
  });

  it('replaces one mixer input and keeps the rest of the array stable', () => {
    const mixer = node('mixer', [input('one', 1), input('two', 1)]);
    const next = applyMixerInputStateToFlow([mixer], {
      mixerId: 'mixer',
      input: input('two', 0.5),
    });
    const inputs = next[0].data.mixerInputs;
    expect(inputs?.[0].gain).toBe(1);
    expect(inputs?.[1].gain).toBe(0.5);
  });

  it('updates one mixer input in the graph snapshot', () => {
    const current = graph();
    current.nodes[0] = { ...current.nodes[0], mixerInputs: [input('one', 1)] };
    const next = applyMixerInputStateToGraph(current, {
      mixerId: 'a',
      input: input('one', 0.25),
    });
    expect(next.nodes[0].mixerInputs?.[0].gain).toBe(0.25);
    expect(next.nodes[1]).toBe(current.nodes[1]);
  });

  it('updates one link and preserves the other links', () => {
    const current = graph();
    const next = applyLinkState(current, {
      id: 'l1',
      gain: 0.4,
      enabled: false,
      native: true,
      nativeRate: 96000,
    });
    expect(next.links[0]).toMatchObject({ gain: 0.4, enabled: false, native: true, nativeRate: 96000 });
    expect(next.links[1]).toBe(current.links[1]);
  });

  it('updates one parameter and preserves unrelated nodes', () => {
    const first = node('a');
    const second = node('b');
    const next = applyParameterUpdate([first, second], {
      id: 'a',
      processor: 0,
      parameter: 0,
      value: 0.75,
    });
    expect(next[0].data.parameters[0].value).toBe(0.75);
    expect(next[1]).toBe(second);
  });

  it('merges source state into the matching node', () => {
    const first = node('tone');
    const second = node('other');
    const next = applySourceState([first, second], {
      id: 'tone',
      toneFrequency: 440,
      toneLevelDb: -12,
    });
    expect(next[0].data.toneFrequency).toBe(440);
    expect(next[0].data.toneLevelDb).toBe(-12);
    expect(next[1]).toBe(second);
  });
});
