export function mount(panel) {
  const open = () => panel.classList.add('open');
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', open);
  } else {
    open();
  }
  if (typeof window.Blazor.addEventListener === 'function') {
    new ResizeObserver(() => panel.classList.toggle('narrow')).observe(panel);
  }
  panel.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') panel.classList.remove('open');
  });
}
