import type { RuseFlowNode } from './flow';
import type {
  GraphSnapshot,
  LinkStateUpdate,
  MixerInputModel,
  MixerInputStateUpdate,
  MixerStateUpdate,
  NodeStateUpdate,
} from './types';

export function applyNodeStateToGraph(graph: GraphSnapshot, update: NodeStateUpdate): GraphSnapshot {
  return {
    ...graph,
    nodes: graph.nodes.map(node => node.id === update.id
      ? {
          ...node,
          volume: update.volume ?? node.volume,
          muted: update.muted ?? node.muted,
          solo: update.solo ?? node.solo,
          enabled: update.enabled ?? node.enabled,
          send: update.send ?? node.send,
          isParallel: update.parallel ?? node.isParallel,
          color: update.color !== undefined ? (update.color || undefined) : node.color,
          accent: update.accent ?? node.accent,
        }
      : node),
  };
}

export function applyNodeStateToFlow(nodes: RuseFlowNode[], update: NodeStateUpdate): RuseFlowNode[] {
  return nodes.map(node => node.id === update.id
    ? {
        ...node,
        data: {
          ...node.data,
          volume: update.volume ?? node.data.volume,
          muted: update.muted ?? node.data.muted,
          solo: update.solo ?? node.data.solo,
          enabled: update.enabled ?? node.data.enabled,
          send: update.send ?? node.data.send,
          isParallel: update.parallel ?? node.data.isParallel,
          color: update.color !== undefined ? (update.color || undefined) : node.data.color,
          accent: update.accent ?? node.data.accent,
        },
      }
    : node);
}

export function applyMixerStateToGraph(graph: GraphSnapshot, update: MixerStateUpdate): GraphSnapshot {
  return {
    ...graph,
    nodes: graph.nodes.map(node => node.id === update.id
      ? {
          ...node,
          mixerWidth: update.width,
          mixerAutoGain: update.autoGain,
          mixerCrossfade: update.crossfade,
        }
      : node),
  };
}

export function applyMixerStateToFlow(nodes: RuseFlowNode[], update: MixerStateUpdate): RuseFlowNode[] {
  return nodes.map(node => node.id === update.id
    ? {
        ...node,
        data: {
          ...node.data,
          mixerWidth: update.width,
          mixerAutoGain: update.autoGain,
          mixerCrossfade: update.crossfade,
        },
      }
    : node);
}

function replaceMixerInput(
  inputs: MixerInputModel[] | undefined,
  input: MixerInputModel,
): MixerInputModel[] | undefined {
  if (!inputs) return inputs;
  const index = inputs.findIndex(item => item.linkId === input.linkId);
  if (index < 0) return inputs;
  const next = inputs.slice();
  next[index] = input;
  return next;
}

export function applyMixerInputStateToGraph(
  graph: GraphSnapshot,
  update: MixerInputStateUpdate,
): GraphSnapshot {
  return {
    ...graph,
    nodes: graph.nodes.map(node => node.id === update.mixerId
      ? { ...node, mixerInputs: replaceMixerInput(node.mixerInputs, update.input) }
      : node),
  };
}

export function applyMixerInputStateToFlow(
  nodes: RuseFlowNode[],
  update: MixerInputStateUpdate,
): RuseFlowNode[] {
  return nodes.map(node => node.id === update.mixerId
    ? {
        ...node,
        data: {
          ...node.data,
          mixerInputs: replaceMixerInput(node.data.mixerInputs, update.input),
        },
      }
    : node);
}

export function applyLinkState(graph: GraphSnapshot, update: LinkStateUpdate): GraphSnapshot {
  return {
    ...graph,
    links: graph.links.map(link => link.id === update.id
      ? {
          ...link,
          gain: update.gain,
          enabled: update.enabled,
          native: update.native,
          nativeRate: update.nativeRate,
        }
      : link),
  };
}

export function applyParameterUpdate(
  nodes: RuseFlowNode[],
  update: { id: string; processor: number; parameter: number; value: number },
): RuseFlowNode[] {
  return nodes.map(node => {
    if (node.id !== update.id) return node;
    const parameters = node.data.parameters.map(parameter =>
      parameter.processorIndex === update.processor && parameter.parameterIndex === update.parameter
        ? { ...parameter, value: update.value }
        : parameter);
    return { ...node, data: { ...node.data, parameters } };
  });
}

export function applySourceState(
  nodes: RuseFlowNode[],
  update: Record<string, unknown> & { id: string },
): RuseFlowNode[] {
  return nodes.map(node => node.id === update.id
    ? { ...node, data: { ...node.data, ...update } }
    : node);
}
