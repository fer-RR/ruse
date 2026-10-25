import { useEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronUp, Plus, Trash2, X } from 'lucide-react';
import ParameterControl from './ParameterControl';
import type { CatalogItem, ChainEditorRequest, ChainElement, ParameterModel } from '../types';

function ModalFrame({ title, onClose, children, className = '' }: {
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  className?: string;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    dialog?.showModal();
    return () => dialog?.close();
  }, []);

  return (
    <dialog ref={ref} className={`modal wide-modal ${className}`} onCancel={event => { event.preventDefault(); onClose(); }} onClick={event => { if (event.target === ref.current) onClose(); }}>
      <div className="modal-frame">
        <header className="modal-header"><h2>{title}</h2><button className="icon-button" aria-label="Close dialog" onClick={onClose}><X size={16} /></button></header>
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

export default function ChainEditorDialog({ request, effects, onSave, onDelete, onClose }: {
  request: ChainEditorRequest;
  effects: CatalogItem[];
  onSave: (request: ChainEditorRequest) => void;
  onDelete?: (name: string) => void;
  onClose: () => void;
}) {
  const [name, setName] = useState(request.name);
  const [elements, setElements] = useState<ChainElement[]>(() => request.elements.map(element => ({
    typeId: element.typeId,
    parameters: { ...element.parameters },
  })));
  const [effectToAdd, setEffectToAdd] = useState(effects[0]?.id ?? '');
  const effectById = new Map(effects.map(effect => [effect.id, effect]));

  const updateElement = (index: number, update: (element: ChainElement) => ChainElement) => {
    setElements(current => current.map((element, itemIndex) => itemIndex === index ? update(element) : element));
  };

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= elements.length) return;
    setElements(current => {
      const next = [...current];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  };

  const save = () => {
    if (!name.trim()) return;
    onSave({ ...request, name: name.trim(), elements });
  };

  return (
    <ModalFrame title={request.previousName ? 'Edit saved chain' : 'New saved chain'} onClose={onClose}>
      <div className="chain-editor">
        <label className="field-label chain-name">Chain name<input autoFocus maxLength={80} value={name} onChange={event => setName(event.target.value)} /></label>
        <div className="chain-editor-heading"><span className="eyebrow">Effects, in order</span><span className="chain-count">{elements.length} effect{elements.length === 1 ? '' : 's'}</span></div>
        <div className="chain-elements">
          {elements.length === 0 && <div className="chain-empty">No effects yet. Add one below, then dial in its parameters.</div>}
          {elements.map((element, index) => {
            const effect = effectById.get(element.typeId);
            const parameters: ParameterModel[] = effect?.parameters ?? [];
            return (
              <section className="chain-element" key={`${element.typeId}-${index}`}>
                <header>
                  <span className="chain-element-index">{index + 1}</span>
                  <strong>{effect?.name ?? element.typeId}</strong>
                  <button className="icon-button compact" title="Move up" aria-label="Move effect up" disabled={index === 0} onClick={() => move(index, -1)}><ChevronUp size={14} /></button>
                  <button className="icon-button compact" title="Move down" aria-label="Move effect down" disabled={index === elements.length - 1} onClick={() => move(index, 1)}><ChevronDown size={14} /></button>
                  <button className="icon-button compact danger-text" title="Remove effect" aria-label="Remove effect" onClick={() => setElements(current => current.filter((_, itemIndex) => itemIndex !== index))}><Trash2 size={14} /></button>
                </header>
                <div className="chain-parameters">
                  {parameters.map(parameter => (
                    <ParameterControl
                      key={parameter.name}
                      parameter={{ ...parameter, value: element.parameters[parameter.name] ?? parameter.value }}
                      value={element.parameters[parameter.name] ?? parameter.value}
                      onChange={value => updateElement(index, current => ({
                        ...current,
                        parameters: { ...current.parameters, [parameter.name]: value },
                      }))}
                    />
                  ))}
                </div>
              </section>
            );
          })}
        </div>
        <div className="chain-add-row">
          <select value={effectToAdd} onChange={event => setEffectToAdd(event.target.value)} aria-label="Effect to add">
            {effects.map(effect => <option key={effect.id} value={effect.id}>{effect.name}</option>)}
          </select>
          <button className="tool-button" disabled={!effectToAdd} onClick={() => {
            const effect = effectById.get(effectToAdd);
            if (!effect) return;
            setElements(current => [...current, { typeId: effect.id, parameters: defaultParameters(effect) }]);
          }}><Plus size={14} /> Add effect</button>
        </div>
      </div>
      <div className="modal-actions">
        {request.previousName && onDelete && <button className="danger-button" onClick={() => onDelete(request.previousName!)}>Delete chain</button>}
        <span className="modal-action-spacer" />
        <button className="tool-button" onClick={onClose}>Cancel</button>
        <button className="primary-button" disabled={!name.trim()} onClick={save}>Save chain</button>
      </div>
    </ModalFrame>
  );
}
