import type { Node } from '@xyflow/react';
import type { NodeModel, TranscriberModelInfo } from './types';

export type RuseNodeData = Record<string, unknown> & NodeModel & {
  highlightMuted: boolean;
  transcriberModels?: TranscriberModelInfo[];
};

export type RuseFlowNode = Node<RuseNodeData, 'ruseNode'>;
