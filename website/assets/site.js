'use strict';
const profiles = {
  desktop: {slots: [['Workstation A', 'Desktop and games'], ['Workstation B', 'A separate desktop']], summary: 'Two desktops and a language model, each with its own GPUs.'},
  models: {slots: [['Speech generation', 'An audio model'], ['Speech recognition', 'An audio model']], summary: 'Speech generation, speech recognition and a language model on one host.'}
};
document.querySelectorAll('[data-profile]').forEach(button => button.addEventListener('click', () => {
  const selected = profiles[button.dataset.profile];
  document.querySelectorAll('[data-profile]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  selected.slots.forEach(([name, detail], index) => {
    const slot = document.querySelector(`[data-slot="${index + 1}"]`);
    const small = document.createElement('small');small.textContent = detail;
    slot.replaceChildren(document.createTextNode(name), small);
  });
  document.getElementById('allocation-status').textContent = selected.summary;
}));
