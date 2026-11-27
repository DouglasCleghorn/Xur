'use strict';

const profiles = {
  desktop: {
    slots: [['Workstation A', 'Desktop and games'], ['Workstation B', 'A separate desktop']],
    summary: 'Two desktops each get a card. The other two cards serve one language model.'
  },
  models: {
    slots: [['Speech generation', 'Text to speech'], ['Speech recognition', 'Speech to text']],
    summary: 'The desktop GPUs now handle speech. The language model keeps its two cards.'
  }
};

const picker = document.querySelector('.profile-buttons');
if (picker) {
  picker.hidden = false;
  const buttons = picker.querySelectorAll('button');
  for (const button of buttons) {
    button.addEventListener('click', () => {
      const profile = profiles[button.dataset.profile];
      buttons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
      profile.slots.forEach(([name, description], index) => {
        const detail = document.createElement('small');
        detail.textContent = description;
        document.querySelector(`[data-slot="${index + 1}"]`).replaceChildren(name, detail);
      });
      document.getElementById('allocation-status').textContent = profile.summary;
    });
  }
}

// Without JavaScript, each illustrated story is an ordinary article in reading order.
const tablist = document.querySelector('.tour-tabs');
if (tablist) {
  const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
  function selectTab(selected) {
    for (const tab of tabs) {
      const active = tab === selected;
      tab.tabIndex = active ? 0 : -1;
      tab.setAttribute('aria-selected', String(active));
      document.getElementById(tab.getAttribute('aria-controls')).hidden = !active;
    }
  }
  for (const [index, tab] of tabs.entries()) {
    const panel = document.getElementById(tab.getAttribute('aria-controls'));
    panel.setAttribute('role', 'tabpanel');
    panel.tabIndex = 0;
    panel.setAttribute('aria-labelledby', tab.id);
    tab.addEventListener('click', () => selectTab(tab));
    tab.addEventListener('keydown', event => {
      const target = {
        ArrowRight: (index + 1) % tabs.length,
        ArrowLeft: (index + tabs.length - 1) % tabs.length,
        Home: 0,
        End: tabs.length - 1
      }[event.key];
      if (target === undefined) return;
      event.preventDefault();
      selectTab(tabs[target]);
      tabs[target].focus();
    });
  }
  selectTab(tabs[0]);
  tablist.hidden = false;
}
