import { useEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronUp, GripVertical, Plus, Trash2, X } from 'lucide-react';
import {
  DndContext, KeyboardSensor, PointerSensor, closestCenter, useSensor, useSensors,
} from '@dnd-kit/core';
import type { DragEndEvent } from '@dnd-kit/core';
import {
  SortableContext, arrayMove, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useI18n } from '../i18n';
import ParameterControl from './ParameterControl';
import type { CatalogItem, ChainEditorRequest, ChainElement, ParameterModel } from '../types';

type EditableElement = ChainElement & { id: string };

let elementIdCounter = 0;
const newElementId = () => `chain-element-${Date.now().toString(36)}-${elementIdCounter++}`;

function ModalFrame({ title, onClose, children, className = '' }: {
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  className?: string;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    dialog?.showModal();
    return () => dialog?.close();
  }, []);

  return (
    <dialog ref={ref} className={`modal wide-modal ${className}`} onCancel={event => { event.preventDefault(); onClose(); }} onClick={event => { if (event.target === ref.current) onClose(); }}>
      <div className="modal-frame">
        <header className="modal-header"><h2>{title}</h2><button className="icon-button" aria-label={t('Close dialog')} onClick={onClose}><X size={16} /></button></header>
        {children}
      </div>
    </dialog>
  );
}

function defaultParameters(effect: CatalogItem | undefined): Record<string, number> {
  const result: Record<string, number> = {};
  for (const parameter of effect?.parameters ?? []) {
    result[parameter.name] = parameter.value;
  }
  return result;
}

function ChainElementRow({ element, index, total, effect, onParameterChange, onMove, onRemove }: {
  element: EditableElement;
  index: number;
  total: number;
  effect: CatalogItem | undefined;
  onParameterChange: (name: string, value: number) => void;
  onMove: (direction: -1 | 1) => void;
  onRemove: () => void;
}) {
  const { t } = useI18n();
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: element.id });
  const style = { transform: CSS.Transform.toString(transform), transition };
  const parameters: ParameterModel[] = effect?.parameters ?? [];

  return (
    <section ref={setNodeRef} style={style} className={`chain-element ${isDragging ? 'dragging' : ''}`}>
      <header>
        <button
          type="button"
          className="chain-drag-handle"
          {...attributes}
          {...listeners}
          title={t('Drag to reorder')}
          aria-label={t('Drag to reorder')}
        ><GripVertical size={14} /></button>
        <span className="chain-element-index">{index + 1}</span>
        <strong>{effect?.name ?? element.typeId}</strong>
        <button type="button" className="icon-button compact" title={t('Move up')} aria-label={t('Move up')} disabled={index === 0} onClick={() => onMove(-1)}><ChevronUp size={14} /></button>
        <button type="button" className="icon-button compact" title={t('Move down')} aria-label={t('Move down')} disabled={index === total - 1} onClick={() => onMove(1)}><ChevronDown size={14} /></button>
        <button type="button" className="icon-button compact danger-text" title={t('Remove effect')} aria-label={t('Remove effect')} onClick={onRemove}><Trash2 size={14} /></button>
      </header>
      <div className="chain-parameters">
        {parameters.map(parameter => (
          <ParameterControl
            key={parameter.name}
            parameter={{ ...parameter, value: element.parameters[parameter.name] ?? parameter.value }}
            value={element.parameters[parameter.name] ?? parameter.value}
            onChange={value => onParameterChange(parameter.name, value)}
          />
        ))}
      </div>
    </section>
  );
}

export default function ChainEditorDialog({ request, effects, onSave, onDelete, onClose }: {
  request: ChainEditorRequest;
  effects: CatalogItem[];
  onSave: (request: ChainEditorRequest) => void;
  onDelete?: (name: string) => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState(request.name);
  const [elements, setElements] = useState<EditableElement[]>(() => request.elements.map(element => ({
    id: newElementId(),
    typeId: element.typeId,
    parameters: { ...element.parameters },
  })));
  const [effectToAdd, setEffectToAdd] = useState(effects[0]?.id ?? '');
  const effectById = new Map(effects.map(effect => [effect.id, effect]));
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const updateElement = (index: number, update: (element: EditableElement) => EditableElement) => {
    setElements(current => current.map((element, itemIndex) => itemIndex === index ? update(element) : element));
  };

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= elements.length) return;
    setElements(current => arrayMove(current, index, target));
  };

  const onDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    setElements(current => {
      const oldIndex = current.findIndex(element => element.id === active.id);
      const newIndex = current.findIndex(element => element.id === over.id);
      return oldIndex < 0 || newIndex < 0 ? current : arrayMove(current, oldIndex, newIndex);
    });
  };

  const save = () => {
    if (!name.trim()) return;
    onSave({
      ...request,
      name: name.trim(),
      elements: elements.map(element => ({ typeId: element.typeId, parameters: element.parameters })),
    });
  };

  return (
    <ModalFrame title={request.previousName ? t('Edit saved chain') : t('New saved chain')} onClose={onClose}>
      <div className="chain-editor">
        <label className="field-label chain-name">{t('Chain name')}<input autoFocus maxLength={80} value={name} onChange={event => setName(event.target.value)} /></label>
        <div className="chain-editor-heading"><span className="eyebrow">{t('Effects, in order')}</span><span className="chain-count">{elements.length === 1 ? t('{count} effect', { count: elements.length }) : t('{count} effects', { count: elements.length })}</span></div>
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
          <div className="chain-elements">
            {elements.length === 0 && <div className="chain-empty">{t('No effects yet. Add one below, then dial in its parameters.')}</div>}
            <SortableContext items={elements.map(element => element.id)} strategy={verticalListSortingStrategy}>
              {elements.map((element, index) => (
                <ChainElementRow
                  key={element.id}
                  element={element}
                  index={index}
                  total={elements.length}
                  effect={effectById.get(element.typeId)}
                  onParameterChange={(parameterName, value) => updateElement(index, current => ({
                    ...current,
                    parameters: { ...current.parameters, [parameterName]: value },
                  }))}
                  onMove={direction => move(index, direction)}
                  onRemove={() => setElements(current => current.filter((_, itemIndex) => itemIndex !== index))}
                />
              ))}
            </SortableContext>
          </div>
        </DndContext>
        <div className="chain-add-row">
          <select value={effectToAdd} onChange={event => setEffectToAdd(event.target.value)} aria-label={t('Effect to add')}>
            {effects.map(effect => <option key={effect.id} value={effect.id}>{effect.name}</option>)}
          </select>
          <button className="tool-button" disabled={!effectToAdd} onClick={() => {
            const effect = effectById.get(effectToAdd);
            if (!effect) return;
            setElements(current => [...current, { id: newElementId(), typeId: effect.id, parameters: defaultParameters(effect) }]);
          }}><Plus size={14} /> {t('Add effect')}</button>
        </div>
      </div>
      <div className="modal-actions">
        {request.previousName && onDelete && <button className="danger-button" onClick={() => onDelete(request.previousName!)}>{t('Delete chain')}</button>}
        <span className="modal-action-spacer" />
        <button className="tool-button" onClick={onClose}>{t('Cancel')}</button>
        <button className="primary-button" disabled={!name.trim()} onClick={save}>{t('Save chain')}</button>
      </div>
    </ModalFrame>
  );
}
