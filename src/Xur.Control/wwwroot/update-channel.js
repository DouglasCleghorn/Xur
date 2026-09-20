(() => {
  const form = document.getElementById('update-channel-form');
  if (!form) return;
  const channel = form.querySelector('[name="channel"]');
  const fields = document.getElementById('local-update-fields');
  const refresh = () => {
    const local = channel.value === 'local';
    fields.hidden = !local;
    fields.disabled = !local;
    fields.querySelectorAll('input, textarea').forEach(input => input.required = local);
  };
  channel.addEventListener('change', refresh);
  refresh();
})();
