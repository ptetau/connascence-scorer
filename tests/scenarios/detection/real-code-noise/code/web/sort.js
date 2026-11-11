export function start(init) {
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  }
  if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
    window.Blazor.addEventListener('enhancedload', init);
  }
}
export function onKey(event, send) {
  if (event.key === 'Enter') send();
}
