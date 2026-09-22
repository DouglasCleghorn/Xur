(() => {
  const input = document.querySelector('#password');
  const toggle = document.querySelector('#show-password');
  if (!input || !toggle) return;
  const slash = toggle.querySelector('.password-eye-slash');
  function setVisible(visible) {
    input.type = visible ? 'text' : 'password';
    const label = visible ? 'Hide password' : 'Show password';
    toggle.setAttribute('aria-label', label);
    toggle.title = label;
    if (slash) slash.toggleAttribute('hidden', !visible);
  }
  toggle.hidden = false;
  toggle.addEventListener('click', () => setVisible(input.type === 'password'));
  // Keep the browser's save-password detection on a password field and avoid
  // restoring a revealed password when navigating back to the form.
  input.form?.addEventListener('submit', () => setVisible(false));
  window.addEventListener('pageshow', () => setVisible(false));
})();
