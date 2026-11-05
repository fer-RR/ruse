import { useEffect, useState } from 'react';
import type { RefObject } from 'react';

/** Tracks whether an element is inside the viewport so canvas work can pause. */
export function useElementVisible<T extends Element>(ref: RefObject<T | null>): boolean {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    const element = ref.current;
    if (!element || typeof IntersectionObserver === 'undefined') {
      setVisible(true);
      return;
    }

    const observer = new IntersectionObserver(entries => {
      setVisible(entries[0]?.isIntersecting ?? false);
    }, { threshold: 0.01 });
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref]);

  return visible;
}
