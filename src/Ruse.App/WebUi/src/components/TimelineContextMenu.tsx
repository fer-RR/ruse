import type { ReactNode, RefObject } from 'react';

export interface TimelineMenuItem {
  id: string;
  label: string;
  icon?: ReactNode;
  disabled?: boolean;
  onClick: () => void;
}

export default function TimelineContextMenu({
  x, y, ariaLabel, items, onClose, menuRef,
}: {
  x: number;
  y: number;
  ariaLabel: string;
  items: TimelineMenuItem[];
  onClose: () => void;
  menuRef: RefObject<HTMLDivElement | null>;
}) {
  return (
    <div
      className="context-layer"
      onPointerDown={event => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div
        ref={menuRef}
        className="context-menu"
        role="menu"
        aria-label={ariaLabel}
        style={{
          left: Math.min(x, window.innerWidth - 232),
          top: Math.min(y, window.innerHeight - 220),
        }}
        onPointerDown={event => event.stopPropagation()}
      >
        {items.map(item => (
          <button
            key={item.id}
            role="menuitem"
            tabIndex={0}
            disabled={item.disabled}
            onClick={() => {
              item.onClick();
              onClose();
            }}
          >
            {item.icon}{item.label}
          </button>
        ))}
      </div>
    </div>
  );
}
