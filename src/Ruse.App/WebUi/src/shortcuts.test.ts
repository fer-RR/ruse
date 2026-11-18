import { describe, expect, it } from 'vitest';
import { isInteractiveTarget, shouldIgnoreShortcut } from './shortcuts';

describe('shortcut guards', () => {
  it('treats form controls and menu items as interactive', () => {
    expect(isInteractiveTarget({ tagName: 'BUTTON' })).toBe(true);
    expect(isInteractiveTarget({ tagName: 'INPUT' })).toBe(true);
    expect(isInteractiveTarget({ tagName: 'DIV', isContentEditable: true })).toBe(true);
    expect(isInteractiveTarget({
      closest: (selector: string) => selector.includes('button') ? {} : null,
    })).toBe(true);
    expect(isInteractiveTarget({ tagName: 'DIV' })).toBe(false);
    expect(isInteractiveTarget(null)).toBe(false);
  });

  it('ignores plain shortcuts on controls but allows modifier shortcuts', () => {
    expect(shouldIgnoreShortcut({ target: { tagName: 'BUTTON' } }, false)).toBe(true);
    expect(shouldIgnoreShortcut({ target: { tagName: 'BUTTON' }, ctrlKey: true }, false)).toBe(false);
    expect(shouldIgnoreShortcut({ target: { tagName: 'DIV' } }, false)).toBe(false);
  });

  it('ignores shortcuts while a modal is open or the event was handled', () => {
    expect(shouldIgnoreShortcut({ target: { tagName: 'DIV' } }, true)).toBe(true);
    expect(shouldIgnoreShortcut({ defaultPrevented: true, target: { tagName: 'DIV' } }, false)).toBe(true);
  });
});
